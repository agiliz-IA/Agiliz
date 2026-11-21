using Agiliz.Core.Models;

namespace Agiliz.Runtime.Data.Entities;

/// <summary>Um lançamento de custo (LLM, ferramenta, transcrição, painel). Substitui configs/billing.json.</summary>
public class CostRecord
{
    public const int DescriptionMaxLength = 300;

    public Guid Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string TenantId { get; set; } = "";
    public CostType Type { get; set; }
    public string Description { get; set; } = "";
    public decimal AmountUsd { get; set; }

    public static CostRecord From(CostEntry entry) => new()
    {
        Id = entry.Id,
        Timestamp = entry.Timestamp.ToUniversalTime(),
        TenantId = entry.TenantId,
        Type = entry.Type,
        Description = entry.Description.Length <= DescriptionMaxLength
            ? entry.Description
            : entry.Description[..DescriptionMaxLength],
        AmountUsd = entry.AmountUsd
    };

    public CostEntry ToEntry() => new()
    {
        Id = Id,
        Timestamp = Timestamp,
        TenantId = TenantId,
        Type = Type,
        Description = Description,
        AmountUsd = AmountUsd
    };
}
