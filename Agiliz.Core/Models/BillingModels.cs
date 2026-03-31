namespace Agiliz.Core.Models;

public record TokenUsage(int Prompt, int Completion)
{
    public int Total => Prompt + Completion;
}

public record ToolExecutionCost(string ToolName, decimal Cost);

public record LlmResponse(string Text, TokenUsage Usage, List<ToolExecutionCost> ToolCosts);

public record ToolResult(string Output, decimal? Cost = null);

public enum CostType
{
    TokensLLM,
    ToolExecution,
    PlatformFee
}

public record CostEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public required string TenantId { get; init; }
    public required CostType Type { get; init; }
    public required string Description { get; init; }
    public required decimal AmountUsd { get; init; }
}
