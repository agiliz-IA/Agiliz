namespace Agiliz.Core.Models;

public sealed record BotConfig
{
    public string TenantId { get; init; } = string.Empty;

    /// <summary>Número WhatsApp no formato "+5511999999999" (apenas dígitos com país)</summary>
    public string WhatsAppNumber { get; init; } = string.Empty;

    /// <summary>Instrução base do bot. Gerada pelo meta-agente.</summary>
    public string SystemPrompt { get; init; } = string.Empty;

    /// <summary>Respostas rápidas para triggers conhecidos (FAQ).</summary>
    public List<FlowEntry> Flows { get; init; } = [];

    public LlmSettings Llm { get; init; } = new();
}

public sealed class FlowEntry
{
    /// <summary>Palavra ou frase que aciona esta resposta.</summary>
    public string Trigger { get; init; } = string.Empty;

    /// <summary>Resposta retornada diretamente, sem chamar o LLM.</summary>
    public string Response { get; init; } = string.Empty;
}

public sealed class LlmSettings
{
    public LlmProvider Provider { get; init; } = LlmProvider.Groq;
    public string Model { get; init; } = "llama-3.3-70b-versatile";

    /// <summary>Limite de tokens na resposta. Mantém o custo baixo.</summary>
    public int MaxTokens { get; init; } = 300;
}

public enum LlmProvider { Groq, Claude }
