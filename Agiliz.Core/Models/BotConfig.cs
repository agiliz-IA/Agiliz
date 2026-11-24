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
    public string? GoogleCalendarId { get; init; }
    public SchedulingSettings Scheduling { get; init; } = new();

    /// <summary>
    /// Integrações ligadas no painel, por id do catálogo (<see cref="Agiliz.Core.Integrations.IntegrationCatalog"/>).
    /// Null = bot criado antes do catálogo: o que vale é derivado do resto do config (ver BotIntegrations).
    /// </summary>
    public Dictionary<string, IntegrationSettings>? Integrations { get; init; }

    /// <summary>Plano contratado (configs/plans/{id}.json): franquia, integrações incluídas e preço.</summary>
    public string? PlanId { get; init; }

    /// <summary>Preço mensal negociado (R$). Quando vazio, vale o preço do plano mais os adicionais ligados.</summary>
    public decimal? MonthlyPriceBrl { get; init; }

    /// <summary>Nome do negócio no portal do cliente (ex.: "Clínica Sorriso"). Vazio = TenantId.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Telefones que entram no portal do cliente com código pelo WhatsApp (só dígitos, com DDI).</summary>
    public List<string> PortalPhones { get; init; } = [];

    /// <summary>Bot pausado pelo cliente no portal: as mensagens ficam registradas, mas o bot não responde.</summary>
    public bool Paused { get; init; }

    /// <summary>Bot de demonstração: limpo toda madrugada e com telefones mascarados no portal.</summary>
    public bool IsDemo { get; init; }

    /// <summary>
    /// Se preenchido, o bot só atende estes números; mensagens de outros são ignoradas por completo
    /// (sem resposta, sem registro, sem custo). Para demonstrar num número pessoal sem atender os contatos dele.
    /// </summary>
    public List<string> RespondOnlyTo { get; init; } = [];
}

public sealed class IntegrationSettings
{
    public bool Enabled { get; init; }

    /// <summary>Valores dos campos da integração (ex.: calendarId).</summary>
    public Dictionary<string, string> Fields { get; init; } = [];

    /// <summary>Ids dos passos do cliente já feitos.</summary>
    public List<string> CompletedSteps { get; init; } = [];

    public DateTimeOffset? UpdatedAt { get; init; }
}

public enum BotType { Generic, Scheduling }

public sealed class SchedulingSettings
{
    public string TimeZone { get; init; } = "America/Sao_Paulo";
    public int AppointmentDurationMinutes { get; init; } = 60;
    public int MinimumNoticeMinutes { get; init; } = 120;
    public List<BusinessHourRule> BusinessHours { get; init; } =
    [
        new() { Day = DayOfWeek.Monday, Start = "08:00", End = "18:00", BreakStart = "12:00", BreakEnd = "13:00" },
        new() { Day = DayOfWeek.Tuesday, Start = "08:00", End = "18:00", BreakStart = "12:00", BreakEnd = "13:00" },
        new() { Day = DayOfWeek.Wednesday, Start = "08:00", End = "18:00", BreakStart = "12:00", BreakEnd = "13:00" },
        new() { Day = DayOfWeek.Thursday, Start = "08:00", End = "18:00", BreakStart = "12:00", BreakEnd = "13:00" },
        new() { Day = DayOfWeek.Friday, Start = "08:00", End = "18:00", BreakStart = "12:00", BreakEnd = "13:00" }
    ];
}

public sealed class BusinessHourRule
{
    public DayOfWeek Day { get; init; }
    public string Start { get; init; } = "08:00";
    public string End { get; init; } = "18:00";
    public string? BreakStart { get; init; }
    public string? BreakEnd { get; init; }
}

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
    public string Model { get; init; } = Agiliz.Core.LLM.ModelCatalog.DefaultGroqModel;

    /// <summary>
    /// Limite de tokens na resposta (inclui o raciocínio dos modelos gpt-oss).
    /// 800 dá folga para o raciocínio "low" sem cortar a resposta; o custo extra é ~US$ 0,0003 por turno.
    /// </summary>
    public int MaxTokens { get; init; } = 800;
}

public enum LlmProvider { Groq, Claude }
