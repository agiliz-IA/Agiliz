namespace Agiliz.Core.Plans;

/// <summary>
/// Mês de uso da franquia, sempre no fuso de Brasília (o mês "vira" à meia-noite local, não em UTC).
/// Formato "yyyy-MM".
/// </summary>
public static class UsageMonth
{
    private static readonly TimeZoneInfo Brasilia = FindBrasilia();

    public static string For(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Brasilia).ToString("yyyy-MM");

    public static string Current() => For(DateTimeOffset.UtcNow);

    public static bool IsValid(string? month) =>
        month is { Length: 7 } && DateTime.TryParseExact(month + "-01", "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out _);

    private static TimeZoneInfo FindBrasilia()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT"); }
    }
}

/// <summary>Uso de um cliente no mês (GET /api/admin/usage).</summary>
public sealed record TenantUsage
{
    public required string TenantId { get; init; }

    /// <summary>Contatos diferentes que mandaram mensagem no mês: é o que conta na franquia.</summary>
    public int Contacts { get; init; }
    public int Messages { get; init; }
}

public enum AllowanceLevel { NoPlan, Ok, Warning, Exceeded }

/// <summary>Situação da franquia: aviso a partir de 80%, excedida a partir de 100%. Nunca bloqueia o atendimento.</summary>
public sealed record AllowanceStatus
{
    public const decimal WarningThreshold = 0.8m;

    public required string TenantId { get; init; }
    public string? PlanName { get; init; }
    public int Contacts { get; init; }
    public int Allowance { get; init; }
    public decimal Ratio => Allowance > 0 ? (decimal)Contacts / Allowance : 0m;
    public int OverageContacts => Math.Max(0, Contacts - Allowance);
    public decimal? OverageRevenueBrl { get; init; }
    public AllowanceLevel Level { get; init; }

    public static AllowanceStatus For(string tenantId, Plan? plan, int contacts)
    {
        if (plan is null)
            return new AllowanceStatus { TenantId = tenantId, Contacts = contacts, Level = AllowanceLevel.NoPlan };

        var ratio = plan.ConversationAllowance > 0 ? (decimal)contacts / plan.ConversationAllowance : 0m;
        var overage = Math.Max(0, contacts - plan.ConversationAllowance);
        return new AllowanceStatus
        {
            TenantId = tenantId,
            PlanName = plan.Name,
            Contacts = contacts,
            Allowance = plan.ConversationAllowance,
            OverageRevenueBrl = plan.OveragePriceBrl is { } price ? overage * price : null,
            Level = ratio >= 1m ? AllowanceLevel.Exceeded : ratio >= WarningThreshold ? AllowanceLevel.Warning : AllowanceLevel.Ok
        };
    }
}
