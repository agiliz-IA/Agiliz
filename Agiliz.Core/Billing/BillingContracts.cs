using Agiliz.Core.Models;

namespace Agiliz.Core.Billing;

/// <summary>
/// Destino dos lançamentos de custo. No Runtime grava no Postgres; o painel envia para o Runtime pela API.
/// <see cref="Record"/> não pode bloquear nem lançar exceção: falha de billing não derruba atendimento.
/// </summary>
public interface ICostRecorder
{
    void Record(CostEntry entry);
}

/// <summary>Resposta de GET /api/admin/billing (Runtime → painel).</summary>
public sealed record BillingReport
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public decimal TotalUsd { get; init; }
    public int EntryCount { get; init; }
    public IReadOnlyList<TenantCostSummary> Tenants { get; init; } = [];

    /// <summary>Total do período por integração, somando todos os clientes.</summary>
    public IReadOnlyList<IntegrationCostSummary> Integrations { get; init; } = [];

    /// <summary>Lançamentos mais recentes do período (limitado por <c>limit</c>); os totais cobrem o período inteiro.</summary>
    public IReadOnlyList<CostEntry> Entries { get; init; } = [];
}

public sealed record TenantCostSummary
{
    public required string TenantId { get; init; }
    public decimal TotalUsd { get; init; }
    public decimal LlmUsd { get; init; }
    public decimal ToolsUsd { get; init; }
    public decimal OtherUsd { get; init; }
    public int Count { get; init; }

    /// <summary>Custo do cliente por integração (id null = lançamento sem integração).</summary>
    public IReadOnlyList<IntegrationCostSummary> ByIntegration { get; init; } = [];
}

public sealed record IntegrationCostSummary
{
    public string? IntegrationId { get; init; }
    public decimal TotalUsd { get; init; }
    public int Count { get; init; }

    /// <summary>Clientes com custo nesta integração (só no total geral).</summary>
    public int TenantCount { get; init; }
}
