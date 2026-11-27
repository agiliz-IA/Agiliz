namespace Agiliz.Wizard.Accounts;

/// <summary>
/// Quem está usando o painel. A conta vem do cookie (lido no middleware) e chega às telas Blazor como
/// parâmetro do <c>Routes</c>. Sem conta = senha de emergência ou painel aberto (ninguém virou Admin): vale como Admin.
/// </summary>
public sealed record PainelUser(Account? Account)
{
    public bool IsAdmin => Account is null || Account.Role == StaffRole.Admin;
    public string Name => Account?.Name ?? "Acesso de emergência";
    public string RoleLabel => Account is null ? "senha do painel" : Account.Role == StaffRole.Admin ? "Admin" : "Vendedor";
    public bool CanOpen(string path) => IsAdmin || !PainelAccess.AdminOnly.Any(p => path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase));
}

public static class PainelAccess
{
    public const string AccountKey = "agiliz.painel.account";

    /// <summary>Telas só de Admin: custos e números internos. O vendedor cria bots, demonstrações, propostas e convida clientes.</summary>
    public static readonly string[] AdminOnly = ["billing", "telemetry"];
}
