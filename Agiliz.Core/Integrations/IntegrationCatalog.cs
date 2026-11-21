using Agiliz.Core.Models;

namespace Agiliz.Core.Integrations;

/// <summary>Se a integração pode ser ligada pelo painel hoje.</summary>
public enum IntegrationStatus
{
    /// <summary>Funciona no código e pode ser ligada por cliente.</summary>
    Available,
    /// <summary>Vendemos, mas a ativação é feita à mão pela equipe (ainda sem suporte no painel).</summary>
    OnRequest,
    /// <summary>Planejada; aparece no catálogo para a venda, sem ativação.</summary>
    ComingSoon
}

/// <summary>Como a integração gera custo para nós.</summary>
public enum IntegrationCostKind
{
    /// <summary>Sem custo direto.</summary>
    None,
    /// <summary>Custo por uso (tokens, minutos de áudio, envio).</summary>
    PerUse,
    /// <summary>Custo por mensagem enviada (WhatsApp oficial).</summary>
    PerMessage
}

public sealed record IntegrationField(string Key, string Label, bool Required, string? Placeholder = null, string? Help = null);

/// <summary>Passo que só o cliente pode fazer (ex.: compartilhar a agenda). O painel mostra como checklist.</summary>
public sealed record IntegrationStep(string Id, string Text);

public sealed record IntegrationDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public IntegrationStatus Status { get; init; } = IntegrationStatus.Available;

    /// <summary>Faz parte de todo bot e não pode ser desligada (ex.: o canal WhatsApp).</summary>
    public bool AlwaysOn { get; init; }

    /// <summary>Só vale para esse tipo de bot (ex.: agenda só em bots de agendamento).</summary>
    public BotType? OnlyFor { get; init; }

    /// <summary>Outras integrações que precisam estar ligadas antes.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];

    public IReadOnlyList<IntegrationField> Fields { get; init; } = [];
    public IReadOnlyList<IntegrationStep> ClientSteps { get; init; } = [];

    /// <summary>Ferramentas (ITool.Name) que o agente recebe quando a integração está ligada.</summary>
    public IReadOnlyList<string> ToolNames { get; init; } = [];

    public IntegrationCostKind CostKind { get; init; } = IntegrationCostKind.None;
    public required string CostNote { get; init; }

    /// <summary>Tem botão "Testar conexão" (o teste roda no Runtime).</summary>
    public bool Testable { get; init; }
}

/// <summary>
/// Integrações que vendemos como serviço. É a fonte única para o painel (catálogo, aba do bot),
/// para o Runtime (quais ferramentas o agente recebe) e para o billing (custo por integração).
/// Integração nova = uma entrada aqui + o código dela.
/// </summary>
public static class IntegrationCatalog
{
    public const string Ai = "ia";
    public const string WhatsApp = "whatsapp";
    public const string WhatsAppOfficial = "whatsapp-oficial";
    public const string GoogleCalendar = "google-agenda";
    public const string Audio = "audio";
    public const string Email = "email";
    public const string Handoff = "transbordo";
    public const string Reminders = "lembretes";
    public const string Pix = "pix";
    public const string ClinicSystems = "sistema-clinica";

    public const string CalendarIdField = "calendarId";

    public static IReadOnlyList<IntegrationDefinition> All { get; } =
    [
        new()
        {
            Id = Ai,
            Name = "Atendimento com IA",
            Description = "O agente que conversa com o cliente final, usando o prompt e as respostas rápidas do bot.",
            AlwaysOn = true,
            CostKind = IntegrationCostKind.PerUse,
            CostNote = "Tokens do modelo (gpt-oss-120b no Groq: US$ 0,15 / 0,60 por milhão de tokens)."
        },
        new()
        {
            Id = WhatsApp,
            Name = "WhatsApp (não oficial)",
            Description = "Canal de mensagens via Evolution API, conectado por QR code a um número do cliente.",
            AlwaysOn = true,
            Testable = true,
            ClientSteps =
            [
                new("chip", "Separar um número (de preferência novo) só para o bot."),
                new("qr", "Escanear o QR code da instância na Evolution com esse número.")
            ],
            CostKind = IntegrationCostKind.None,
            CostNote = "Sem custo por mensagem; entra na hospedagem. Risco de bloqueio do número pela Meta."
        },
        new()
        {
            Id = WhatsAppOfficial,
            Name = "WhatsApp oficial (Cloud API)",
            Description = "Canal oficial da Meta, sem risco de bloqueio. A ativação ainda é manual.",
            Status = IntegrationStatus.OnRequest,
            ClientSteps =
            [
                new("meta-business", "Verificar a empresa no Gerenciador de Negócios da Meta."),
                new("numero", "Liberar um número que não esteja em uso no app do WhatsApp."),
                new("pagamento", "Cadastrar forma de pagamento na conta do WhatsApp Business.")
            ],
            CostKind = IntegrationCostKind.PerMessage,
            CostNote = "1.000 mensagens de atendimento grátis por mês por número; o excedente e as mensagens de utilidade são cobrados pela Meta."
        },
        new()
        {
            Id = GoogleCalendar,
            Name = "Google Agenda",
            Description = "O agente consulta horários livres, marca, remarca e cancela consultas na agenda do cliente.",
            OnlyFor = BotType.Scheduling,
            Testable = true,
            Fields =
            [
                new(CalendarIdField, "ID da agenda", Required: true,
                    Placeholder: "nome@gmail.com ou ...@group.calendar.google.com",
                    Help: "Google Agenda → Configurações da agenda → Integrar agenda → ID da agenda.")
            ],
            ClientSteps =
            [
                new("compartilhar", "Compartilhar a agenda com a conta de serviço do Agiliz, com permissão \"Fazer alterações nos eventos\" (o e-mail aparece no teste de conexão)."),
                new("id", "Enviar o ID da agenda para a equipe.")
            ],
            ToolNames = ["verificar_agenda", "marcar_agenda", "confirmar_agendamento", "cancelar_agendamento", "reagendar_agendamento"],
            CostKind = IntegrationCostKind.None,
            CostNote = "API do Google sem custo nesse volume."
        },
        new()
        {
            Id = Reminders,
            Name = "Lembretes na véspera",
            Description = "Mensagem automática 24 h antes da consulta, com opções para confirmar ou remarcar.",
            OnlyFor = BotType.Scheduling,
            Requires = [GoogleCalendar],
            CostKind = IntegrationCostKind.None,
            CostNote = "Sem custo no WhatsApp não oficial; no oficial vira mensagem de utilidade cobrada."
        },
        new()
        {
            Id = Audio,
            Name = "Transcrição de áudio",
            Description = "Notas de voz do cliente viram texto antes de ir para o agente.",
            Testable = true,
            CostKind = IntegrationCostKind.PerUse,
            CostNote = "Whisper no Groq: US$ 0,04 por hora de áudio (mínimo de 10 s por áudio)."
        },
        new()
        {
            Id = Email,
            Name = "Envio de e-mail",
            Description = "O agente pode enviar e-mails (confirmações, resumos) pelo SMTP configurado no servidor.",
            Testable = true,
            ToolNames = ["send_email"],
            CostKind = IntegrationCostKind.PerUse,
            CostNote = "Custo do provedor de e-mail; hoje lançado como US$ 0,02 por envio."
        },
        new()
        {
            Id = Handoff,
            Name = "Transbordo para humano",
            Description = "O agente pausa o bot e passa a conversa para a equipe do cliente, que responde pelo painel.",
            ToolNames = ["chamar_atendente"],
            CostKind = IntegrationCostKind.None,
            CostNote = "Sem custo."
        },
        new()
        {
            Id = Pix,
            Name = "Pix (sinal e pré-pagamento)",
            Description = "Cobrança de sinal na hora de marcar.",
            Status = IntegrationStatus.ComingSoon,
            CostKind = IntegrationCostKind.PerUse,
            CostNote = "Tarifa do provedor de pagamento."
        },
        new()
        {
            Id = ClinicSystems,
            Name = "Sistemas de clínica",
            Description = "Agenda direto no sistema de gestão da clínica, sem Google Agenda.",
            Status = IntegrationStatus.ComingSoon,
            CostKind = IntegrationCostKind.None,
            CostNote = "Depende do sistema."
        }
    ];

    private static readonly Dictionary<string, IntegrationDefinition> ById =
        All.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, IntegrationDefinition> ByTool =
        All.SelectMany(i => i.ToolNames.Select(t => (Tool: t, Integration: i)))
           .ToDictionary(x => x.Tool, x => x.Integration, StringComparer.OrdinalIgnoreCase);

    public static IntegrationDefinition? Find(string? id) =>
        id is not null && ById.TryGetValue(id, out var def) ? def : null;

    /// <summary>Integração dona de uma ferramenta (para lançar o custo dela no lugar certo).</summary>
    public static IntegrationDefinition? ForTool(string toolName) =>
        ByTool.TryGetValue(toolName, out var def) ? def : null;
}
