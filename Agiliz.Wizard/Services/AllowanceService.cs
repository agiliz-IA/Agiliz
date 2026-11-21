using Agiliz.Core.Plans;

namespace Agiliz.Wizard.Services;

public sealed record AllowanceReport(string Month, IReadOnlyList<AllowanceStatus> Bots, string? Error)
{
    /// <summary>Bots com 80% ou mais da franquia usada.</summary>
    public IEnumerable<AllowanceStatus> Alerts =>
        Bots.Where(b => b.Level is AllowanceLevel.Warning or AllowanceLevel.Exceeded).OrderByDescending(b => b.Ratio);

    public AllowanceStatus? For(string tenantId) =>
        Bots.FirstOrDefault(b => string.Equals(b.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Junta o uso do mês (Runtime) com o plano de cada bot: aviso a 80% e 100%, sem bloquear nada.</summary>
public sealed class AllowanceService(RuntimeApiClient runtime, BotIntegrationService bots, PlanStore plans, ILogger<AllowanceService> logger)
{
    public async Task<AllowanceReport> GetAsync(string? month = null, CancellationToken ct = default)
    {
        month ??= UsageMonth.Current();
        var usage = new Dictionary<string, TenantUsage>(StringComparer.OrdinalIgnoreCase);
        string? error = null;
        try
        {
            foreach (var u in await runtime.GetUsageAsync(month, ct))
                usage[u.TenantId] = u;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Uso do mês indisponível");
            error = ex.Message;
        }

        var statuses = bots.Bots().Values
            .Select(b => AllowanceStatus.For(b.TenantId, plans.Get(b.PlanId), usage.GetValueOrDefault(b.TenantId)?.Contacts ?? 0))
            .OrderBy(s => s.TenantId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AllowanceReport(month, statuses, error);
    }
}
