using Agiliz.Core.Config;
using Agiliz.Core.Integrations;
using Agiliz.Core.Models;
using Agiliz.Core.Plans;

namespace Agiliz.Wizard.Services;

public sealed record QuizAnswers(Dictionary<string, string>? Answers);

public sealed record LostReason(string? Reason);

public sealed record ProposalInput
{
    public string BusinessName { get; init; } = "";
    public string? ContactName { get; init; }
    public string? ContactPhone { get; init; }
    public string? Notes { get; init; }
    public Dictionary<string, string>? Answers { get; init; }
}

/// <summary>Quiz de venda: recomendação, propostas salvas e o bot criado a partir delas.</summary>
public sealed class ProposalService(IConfiguration configuration, PlanStore plans, CostSettings costs, RuntimeApiClient runtime)
{
    public const string FolderName = "proposals";

    private string ConfigsDir => BotConfigLoader.ResolveConfigsDir(configuration);
    private JsonFileStore<Proposal> Files => new(ConfigsDir, FolderName);

    public QuizRecommendation Evaluate(IReadOnlyDictionary<string, string>? answers) =>
        QuizEngine.Evaluate(answers, plans.List(), costs.UsdToBrl, costs.FixedMonthlyBrl);

    public IReadOnlyList<Proposal> List() => Files.List().OrderByDescending(p => p.CreatedAt).ToList();

    public Proposal? Get(string id) => Files.Load(id);

    public ProposalStats Stats()
    {
        var all = Files.List();
        return new ProposalStats(all.Count,
            all.Count(p => p.Status == ProposalStatus.Open),
            all.Count(p => p.Status == ProposalStatus.Converted),
            all.Count(p => p.Status == ProposalStatus.Lost));
    }

    /// <summary>Cria (id null) ou atualiza a proposta, com a foto da recomendação atual.</summary>
    public (Proposal? Proposal, string? Error) Save(string? id, ProposalInput input)
    {
        if (string.IsNullOrWhiteSpace(input.BusinessName)) return (null, "Informe o nome do negócio.");

        var existing = id is null ? null : Files.Load(id);
        if (id is not null && existing is null) return (null, "Proposta não encontrada.");
        if (existing?.Status == ProposalStatus.Converted) return (null, "Proposta já virou cliente; não dá para editar.");

        var answers = QuizEngine.Clean(input.Answers);
        var recommendation = Evaluate(answers);
        var now = DateTimeOffset.UtcNow;

        var proposal = new Proposal
        {
            Id = existing?.Id ?? NewId(now),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
            BusinessName = input.BusinessName.Trim(),
            ContactName = Trim(input.ContactName),
            ContactPhone = Trim(input.ContactPhone),
            Notes = Trim(input.Notes),
            Answers = answers,
            PlanId = recommendation.Plan?.Id,
            PlanName = recommendation.Plan?.Name,
            MonthlyPriceBrl = recommendation.MonthlyPriceBrl,
            EstimatedCostBrl = recommendation.Complete ? recommendation.EstimatedCostBrl : null,
            EstimatedContacts = recommendation.EstimatedContacts,
            Status = existing?.Status ?? ProposalStatus.Open,
            LostReason = existing?.LostReason,
            ClosedAt = existing?.ClosedAt
        };
        Files.Save(proposal.Id, proposal);
        return (proposal, null);
    }

    public string? MarkLost(string id, string? reason)
    {
        var p = Files.Load(id);
        if (p is null) return "Proposta não encontrada.";
        if (p.Status == ProposalStatus.Converted) return "Proposta já virou cliente.";
        Files.Save(id, p with { Status = ProposalStatus.Lost, LostReason = Trim(reason), ClosedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        return null;
    }

    public string? Reopen(string id)
    {
        var p = Files.Load(id);
        if (p is null) return "Proposta não encontrada.";
        if (p.Status == ProposalStatus.Converted) return "Proposta já virou cliente.";
        Files.Save(id, p with { Status = ProposalStatus.Open, LostReason = null, ClosedAt = null, UpdatedAt = DateTimeOffset.UtcNow });
        return null;
    }

    /// <summary>
    /// Depois que o bot é salvo pelo assistente: aplica plano, integrações recomendadas e LGPD,
    /// pede reload ao Runtime e marca a proposta como convertida.
    /// </summary>
    public async Task<SaveOutcome> ApplyToBotAsync(string proposalId, string tenantId, CancellationToken ct = default)
    {
        var proposal = Files.Load(proposalId);
        if (proposal is null) return SaveOutcome.Fail("Proposta não encontrada.");
        if (!BotConfigLoader.Exists(ConfigsDir, tenantId)) return SaveOutcome.Fail("Bot não encontrado.");

        var config = BotConfigLoader.Load(ConfigsDir, tenantId);
        var recommendation = Evaluate(proposal.Answers);
        var integrations = BotIntegrations.Materialize(config);

        foreach (var def in IntegrationCatalog.All.Where(d => d.Status == IntegrationStatus.Available && !d.AlwaysOn))
        {
            var current = integrations.GetValueOrDefault(def.Id) ?? new IntegrationSettings();
            integrations[def.Id] = new IntegrationSettings
            {
                Enabled = recommendation.IntegrationsToEnable.Contains(def.Id),
                Fields = current.Fields,
                CompletedSteps = current.CompletedSteps,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        var planId = plans.Get(proposal.PlanId) is not null ? proposal.PlanId : recommendation.Plan?.Id;

        // O contato da proposta passa a entrar no portal do cliente (código pelo WhatsApp).
        var portalPhones = config.PortalPhones.ToList();
        var contact = Agiliz.Wizard.Portal.PortalAuthService.NormalizePhone(proposal.ContactPhone);
        if (contact.Length is 12 or 13 && !portalPhones.Contains(contact)) portalPhones.Add(contact);

        BotConfigLoader.Save(ConfigsDir, config with
        {
            Integrations = integrations,
            PlanId = planId,
            LgpdConsentRequired = config.LgpdConsentRequired || recommendation.LgpdConsentRequired,
            DisplayName = config.DisplayName ?? proposal.BusinessName,
            PortalPhones = portalPhones
        });

        Files.Save(proposal.Id, proposal with
        {
            Status = ProposalStatus.Converted,
            TenantId = tenantId,
            ClosedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var reload = await runtime.ReloadAsync(ct);
        return new SaveOutcome(true, RuntimeReloaded: reload.Ok, RuntimeError: reload.Error);
    }

    /// <summary>Sugestão de tenantId a partir do nome do negócio ("Clínica Sorriso" → "clinica-sorriso").</summary>
    public static string SuggestTenantId(string businessName)
    {
        var normalized = businessName.Normalize(System.Text.NormalizationForm.FormD);
        var chars = normalized
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray();
        var slug = System.Text.RegularExpressions.Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        if (slug.Length > 64) slug = slug[..64].Trim('-');
        return BotConfigLoader.IsValidTenantId(slug) ? slug : "novo-bot";
    }

    private static string NewId(DateTimeOffset now) => $"p-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..6]}";

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
