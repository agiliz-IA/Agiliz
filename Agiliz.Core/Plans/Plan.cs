using Agiliz.Core.Config;
using Agiliz.Core.Integrations;
using Agiliz.Core.Models;

namespace Agiliz.Core.Plans;

/// <summary>Tipo de venda (plano). Editado no painel, gravado em configs/plans/{id}.json.</summary>
public sealed record Plan
{
    /// <summary>Na API vem da rota (PUT /api/plans/{id}); o PlanStore valida.</summary>
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }

    public decimal MonthlyPriceBrl { get; init; }

    /// <summary>Franquia: contatos diferentes atendidos no mês.</summary>
    public int ConversationAllowance { get; init; }

    /// <summary>Preço por contato acima da franquia (null = só aviso, sem cobrança).</summary>
    public decimal? OveragePriceBrl { get; init; }

    /// <summary>Quantos profissionais o plano cobre (null = sem limite). Usado pelo quiz.</summary>
    public int? MaxProfessionals { get; init; }

    /// <summary>Integrações que já vêm no preço.</summary>
    public List<string> IncludedIntegrations { get; init; } = [];

    /// <summary>Integrações vendidas à parte: id → preço mensal em R$.</summary>
    public Dictionary<string, decimal> AddOnPricesBrl { get; init; } = [];

    /// <summary>Valores ainda não aprovados (o painel mostra o aviso).</summary>
    public bool Provisional { get; init; }

    public bool Active { get; init; } = true;
    public int SortOrder { get; init; }

    public bool Includes(string integrationId) =>
        IntegrationCatalog.Find(integrationId)?.AlwaysOn == true ||
        IncludedIntegrations.Contains(integrationId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Preço do adicional, ou null se a integração está incluída ou não é vendida à parte.</summary>
    public decimal? AddOnPrice(string integrationId)
    {
        if (Includes(integrationId)) return null;
        foreach (var (key, price) in AddOnPricesBrl)
            if (string.Equals(key, integrationId, StringComparison.OrdinalIgnoreCase))
                return price;
        return null;
    }
}

/// <summary>Planos em configs/plans/. Na primeira vez cria três planos provisórios para editar no painel.</summary>
public sealed class PlanStore
{
    public const string FolderName = "plans";
    private readonly JsonFileStore<Plan> _files;

    public PlanStore(string configsDir)
    {
        _files = new JsonFileStore<Plan>(configsDir, FolderName);
    }

    public IReadOnlyList<Plan> List()
    {
        SeedIfMissing();
        return _files.List().OrderBy(p => p.SortOrder).ThenBy(p => p.MonthlyPriceBrl).ToList();
    }

    public Plan? Get(string? id) => id is null ? null : List().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Valida e salva. Retorna o erro em português, ou null se salvou.</summary>
    public string? Save(Plan plan)
    {
        if (!BotConfigLoader.IsValidTenantId(plan.Id)) return "Id do plano inválido: use letras, números, '-' ou '_'.";
        if (string.IsNullOrWhiteSpace(plan.Name)) return "Dê um nome ao plano.";
        if (plan.MonthlyPriceBrl < 0) return "O preço não pode ser negativo.";
        if (plan.ConversationAllowance <= 0) return "A franquia precisa ser maior que zero.";
        if (plan.OveragePriceBrl is < 0) return "O preço do excedente não pode ser negativo.";
        if (plan.MaxProfessionals is <= 0) return "O número de profissionais precisa ser maior que zero (ou vazio para sem limite).";
        if (plan.AddOnPricesBrl.Values.Any(v => v < 0)) return "Preço de adicional não pode ser negativo.";

        var unknown = plan.IncludedIntegrations.Concat(plan.AddOnPricesBrl.Keys)
            .Where(id => IntegrationCatalog.Find(id) is null).ToList();
        if (unknown.Count > 0) return $"Integração fora do catálogo: {string.Join(", ", unknown)}.";

        SeedIfMissing();
        _files.Save(plan.Id, plan with { Name = plan.Name.Trim() });
        return null;
    }

    public bool Delete(string id) => _files.Delete(id);

    private void SeedIfMissing()
    {
        // A pasta existir = já semeado. Apagar todos os planos não recria os provisórios.
        if (Directory.Exists(_files.Dir)) return;
        Directory.CreateDirectory(_files.Dir);
        foreach (var plan in ProvisionalPlans)
            _files.Save(plan.Id, plan);
    }

    /// <summary>
    /// Ponto de partida para editar no painel. Valores provisórios dentro da faixa dos concorrentes
    /// (R$ 150–400/mês), a confirmar com o Bruno.
    /// </summary>
    public static IReadOnlyList<Plan> ProvisionalPlans { get; } =
    [
        new()
        {
            Id = "essencial", Name = "Essencial", SortOrder = 1, Provisional = true,
            Description = "Um profissional, agenda no Google e transbordo para a equipe.",
            MonthlyPriceBrl = 149m, ConversationAllowance = 150, OveragePriceBrl = 0.80m, MaxProfessionals = 1,
            IncludedIntegrations = [IntegrationCatalog.GoogleCalendar, IntegrationCatalog.Handoff],
            AddOnPricesBrl = new() { [IntegrationCatalog.Audio] = 20m, [IntegrationCatalog.Reminders] = 20m, [IntegrationCatalog.Email] = 10m }
        },
        new()
        {
            Id = "profissional", Name = "Profissional", SortOrder = 2, Provisional = true,
            Description = "Até 3 profissionais, com áudio, lembretes e e-mail.",
            MonthlyPriceBrl = 249m, ConversationAllowance = 400, OveragePriceBrl = 0.60m, MaxProfessionals = 3,
            IncludedIntegrations = [IntegrationCatalog.GoogleCalendar, IntegrationCatalog.Handoff, IntegrationCatalog.Audio, IntegrationCatalog.Reminders, IntegrationCatalog.Email]
        },
        new()
        {
            Id = "clinica", Name = "Clínica", SortOrder = 3, Provisional = true,
            Description = "Equipe grande e volume alto; tudo incluído.",
            MonthlyPriceBrl = 399m, ConversationAllowance = 1000, OveragePriceBrl = 0.40m, MaxProfessionals = null,
            IncludedIntegrations = [IntegrationCatalog.GoogleCalendar, IntegrationCatalog.Handoff, IntegrationCatalog.Audio, IntegrationCatalog.Reminders, IntegrationCatalog.Email]
        }
    ];
}

/// <summary>Preço efetivo de um bot: negociado (BotConfig.MonthlyPriceBrl) ou plano + adicionais ligados.</summary>
public static class BotPricing
{
    public static decimal? MonthlyPriceBrl(BotConfig config, Plan? plan)
    {
        if (config.MonthlyPriceBrl is { } negotiated) return negotiated;
        if (plan is null) return null;
        return plan.MonthlyPriceBrl + BotIntegrations.Enabled(config).Sum(i => plan.AddOnPrice(i.Id) ?? 0m);
    }

    /// <summary>Integrações ligadas no bot que não estão no plano nem têm preço de adicional.</summary>
    public static IReadOnlyList<IntegrationDefinition> OutsidePlan(BotConfig config, Plan plan) =>
        BotIntegrations.Enabled(config).Where(i => !plan.Includes(i.Id) && plan.AddOnPrice(i.Id) is null).ToList();
}
