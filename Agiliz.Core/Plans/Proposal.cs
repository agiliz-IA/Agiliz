namespace Agiliz.Core.Plans;

public enum ProposalStatus { Open, Converted, Lost }

/// <summary>
/// Quiz salvo durante a venda (configs/proposals/{id}.json): dá para retomar depois e medir quantas
/// propostas viram cliente. Guarda nome e telefone do contato do lead: não versionar a pasta configs.
/// </summary>
public sealed record Proposal
{
    public required string Id { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public required string BusinessName { get; init; }
    public string? ContactName { get; init; }
    public string? ContactPhone { get; init; }
    public string? Notes { get; init; }

    public Dictionary<string, string> Answers { get; init; } = [];

    // Foto da recomendação no momento em que foi salva (o plano pode mudar depois).
    public string? PlanId { get; init; }
    public string? PlanName { get; init; }
    public decimal? MonthlyPriceBrl { get; init; }
    public decimal? EstimatedCostBrl { get; init; }
    public int EstimatedContacts { get; init; }

    public ProposalStatus Status { get; init; } = ProposalStatus.Open;

    /// <summary>Bot criado a partir da proposta.</summary>
    public string? TenantId { get; init; }
    public DateTimeOffset? ClosedAt { get; init; }
    public string? LostReason { get; init; }
}

public sealed record ProposalStats(int Total, int Open, int Converted, int Lost)
{
    /// <summary>Convertidas sobre o total de propostas (null sem propostas).</summary>
    public decimal? ConversionRate => Total == 0 ? null : (decimal)Converted / Total;
}
