namespace Agiliz.Runtime.Data.Entities;

public class SchedulingUser
{
    public string Phone { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? PaymentMethod { get; set; }
    public DateTimeOffset? LgpdConsentDate { get; set; }
}
