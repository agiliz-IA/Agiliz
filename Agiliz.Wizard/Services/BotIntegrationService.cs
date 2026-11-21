using Agiliz.Core.Config;
using Agiliz.Core.Integrations;
using Agiliz.Core.Models;

namespace Agiliz.Wizard.Services;

public sealed record IntegrationState
{
    public required IntegrationDefinition Definition { get; init; }
    public bool Enabled { get; init; }

    /// <summary>Por que não dá para ligar (null = dá).</summary>
    public string? BlockedReason { get; init; }

    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> CompletedSteps { get; init; } = [];
}

public sealed record BotIntegrationsView
{
    public required string TenantId { get; init; }
    public BotType Type { get; init; }
    public decimal? MonthlyPriceBrl { get; init; }
    public IReadOnlyList<IntegrationState> Integrations { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record IntegrationUpdate
{
    public bool Enabled { get; init; }
    public Dictionary<string, string>? Fields { get; init; }
    public List<string>? CompletedSteps { get; init; }
}

public sealed record PriceUpdate(decimal? MonthlyPriceBrl);

public sealed record IntegrationUsage(IntegrationDefinition Definition, IReadOnlyList<string> Tenants);

public sealed record SaveOutcome(bool Ok, string? Error = null, bool RuntimeReloaded = false, string? RuntimeError = null)
{
    public static SaveOutcome Fail(string error) => new(false, error);
}

/// <summary>
/// Liga, desliga e configura integrações por bot (aba Integrações). Grava no config do bot e pede
/// reload ao Runtime, então a mudança vale na próxima mensagem do cliente.
/// </summary>
public sealed class BotIntegrationService(IConfiguration configuration, RuntimeApiClient runtime)
{
    private string ConfigsDir => BotConfigLoader.ResolveConfigsDir(configuration);

    /// <summary>Quais clientes usam cada integração do catálogo.</summary>
    public IReadOnlyList<IntegrationUsage> Usage()
    {
        var bots = LoadAll();
        return IntegrationCatalog.All
            .Select(def => new IntegrationUsage(def, bots.Where(b => BotIntegrations.IsEnabled(b, def.Id)).Select(b => b.TenantId).ToList()))
            .ToList();
    }

    public BotIntegrationsView? Get(string tenantId)
    {
        if (!BotConfigLoader.Exists(ConfigsDir, tenantId)) return null;
        var config = BotConfigLoader.Load(ConfigsDir, tenantId);
        var materialized = BotIntegrations.Materialize(config);

        var states = IntegrationCatalog.All.Select(def =>
        {
            var settings = materialized.GetValueOrDefault(def.Id) ?? config.Integrations?.GetValueOrDefault(def.Id);
            return new IntegrationState
            {
                Definition = def,
                Enabled = BotIntegrations.IsEnabled(config, def.Id),
                BlockedReason = def.AlwaysOn ? null : BotIntegrations.BlockedReason(config, def),
                Fields = settings?.Fields ?? new Dictionary<string, string>(),
                CompletedSteps = settings?.CompletedSteps ?? []
            };
        }).ToList();

        return new BotIntegrationsView
        {
            TenantId = config.TenantId,
            Type = config.Type,
            MonthlyPriceBrl = config.MonthlyPriceBrl,
            Integrations = states,
            Warnings = Warnings(config)
        };
    }

    public async Task<SaveOutcome> UpdateAsync(string tenantId, string integrationId, IntegrationUpdate update, CancellationToken ct = default)
    {
        if (!BotConfigLoader.Exists(ConfigsDir, tenantId)) return SaveOutcome.Fail("Bot não encontrado.");
        var def = IntegrationCatalog.Find(integrationId);
        if (def is null) return SaveOutcome.Fail("Integração fora do catálogo.");
        if (def.Status != IntegrationStatus.Available) return SaveOutcome.Fail(BotIntegrations.BlockedReason(new BotConfig(), def)!);

        var config = BotConfigLoader.Load(ConfigsDir, tenantId);
        var enabled = def.AlwaysOn || update.Enabled;

        if (enabled && !def.AlwaysOn && BotIntegrations.BlockedReason(config, def) is { } blocked)
            return SaveOutcome.Fail(blocked);

        var fields = def.Fields
            .Select(f => (f.Key, Value: update.Fields?.GetValueOrDefault(f.Key)?.Trim()))
            .Where(f => !string.IsNullOrEmpty(f.Value))
            .ToDictionary(f => f.Key, f => f.Value!, StringComparer.OrdinalIgnoreCase);

        var missing = def.Fields.Where(f => f.Required && !fields.ContainsKey(f.Key)).Select(f => f.Label).ToList();
        if (enabled && missing.Count > 0)
            return SaveOutcome.Fail($"Preencha: {string.Join(", ", missing)}.");

        var stepIds = def.ClientSteps.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var steps = (update.CompletedSteps ?? []).Where(stepIds.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var integrations = BotIntegrations.Materialize(config);
        integrations[def.Id] = new IntegrationSettings
        {
            Enabled = enabled,
            Fields = fields,
            CompletedSteps = steps,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        return await SaveAsync(config with { Integrations = integrations }, ct);
    }

    public async Task<SaveOutcome> SetPriceAsync(string tenantId, decimal? monthlyPriceBrl, CancellationToken ct = default)
    {
        if (!BotConfigLoader.Exists(ConfigsDir, tenantId)) return SaveOutcome.Fail("Bot não encontrado.");
        if (monthlyPriceBrl is < 0 or > 100_000) return SaveOutcome.Fail("Preço fora do intervalo (0 a 100.000).");

        var config = BotConfigLoader.Load(ConfigsDir, tenantId);
        return await SaveAsync(config with { MonthlyPriceBrl = monthlyPriceBrl }, ct);
    }

    /// <summary>Preço mensal de cada bot (para a margem no Financeiro).</summary>
    public IReadOnlyDictionary<string, BotConfig> Bots() =>
        LoadAll().ToDictionary(b => b.TenantId, StringComparer.OrdinalIgnoreCase);

    private async Task<SaveOutcome> SaveAsync(BotConfig config, CancellationToken ct)
    {
        BotConfigLoader.Save(ConfigsDir, config);
        var reload = await runtime.ReloadAsync(ct);
        return new SaveOutcome(true, RuntimeReloaded: reload.Ok, RuntimeError: reload.Error);
    }

    private List<BotConfig> LoadAll()
    {
        var bots = new List<BotConfig>();
        foreach (var id in BotConfigLoader.ListTenants(ConfigsDir))
        {
            try { bots.Add(BotConfigLoader.Load(ConfigsDir, id)); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { }
        }
        return bots;
    }

    private static List<string> Warnings(BotConfig config)
    {
        var warnings = new List<string>();
        if (config.Type == BotType.Scheduling && !BotIntegrations.IsEnabled(config, IntegrationCatalog.GoogleCalendar))
            warnings.Add("Bot de agendamento sem Google Agenda: o agente não consegue consultar nem marcar horários.");
        if (BotIntegrations.IsEnabled(config, IntegrationCatalog.GoogleCalendar) && BotIntegrations.CalendarId(config) is null)
            warnings.Add("Google Agenda ligada sem ID da agenda: preencha o ID para o agente conseguir marcar.");
        return warnings;
    }
}
