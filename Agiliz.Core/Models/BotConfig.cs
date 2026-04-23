namespace Agiliz.Core.Models;

public sealed record BotConfig
{
    public string TenantId { get; init; } = string.Empty;

    public BotType Type { get; init; } = BotType.Generic;

    /// <summary>Número WhatsApp no formato "+5511999999999" (apenas dígitos com país)</summary>
    public string WhatsAppNumber { get; init; } = string.Empty;

    /// <summary>Instrução base do bot. Gerada pelo meta-agente.</summary>
    public string SystemPrompt { get; init; } = string.Empty;

    /// <summary>Respostas rápidas para triggers conhecidos (FAQ).</summary>
    public List<FlowEntry> Flows { get; init; } = [];

    public LlmSettings Llm { get; init; } = new();
    
    public GuardRailSettings GuardRails { get; init; } = new();

    public List<string> PaymentMethods { get; init; } = [];
    public bool LgpdConsentRequired { get; init; } = false;
}

public enum BotType { Generic, Scheduling }

public sealed class GuardRailSettings
{
    public int MaxSessionTurns { get; init; } = 25;
    public decimal MaxSessionSpendUsd { get; init; } = 0.50m;
    public string FallbackMessage { get; init; } = "Agradecemos muito pelo seu contato! Para garantir o melhor atendimento e organizar nossas demandas, precisamos pausar nossa conversa por agora. Se precisar de mais alguma coisa, por favor, retorne o contato mais tarde ou aguarde que um de nossos humanos falará com você! Um abraço!";
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
