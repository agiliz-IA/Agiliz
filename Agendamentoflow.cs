/*
 * ============================================================
 *  FLUXO DE AGENDAMENTO — WhatsApp Bot / Google Agenda
 *  Arquivo de referência para implementação no Antigravity
 * ============================================================
 *
 * DIAGRAMA (Mermaid — cole em https://mermaid.live para visualizar)
 * -----------------------------------------------------------------
 *
 * flowchart TD
 *
 *     INICIO([INÍCIO])
 *     INICIO --> ACESSO
 *
 *     ACESSO["📱 Cliente acessa via link ou QR Code (WhatsApp)"]
 *     ACESSO --> IDENTIFICACAO
 *
 *     IDENTIFICACAO["🤖 Agente identifica o cliente\ne pergunta se deseja agendar"]
 *     IDENTIFICACAO --> DECISAO_AGENDAR
 *
 *     DECISAO_AGENDAR{Deseja agendar?}
 *     DECISAO_AGENDAR -- SIM --> COLETAR
 *     DECISAO_AGENDAR -- NÃO --> GUARDRAIL
 *
 *     GUARDRAIL["⚠️ Bot pergunta se pode ajudar\ncom algo simples\n(guardrail: foco em agendamento/atendimento,\nsem conversas abertas)"]
 *     GUARDRAIL --> ENCERRAR_NAO
 *     ENCERRAR_NAO([🔴 ENCERRAR])
 *
 *     COLETAR["📋 Coletar dados do cliente:\n· Nome completo\n· Dias da semana preferidos\n· Horário preferido\n· Contato: celular ou e-mail\n· Forma de pagamento\n💾 Dados persistidos para reagendamentos"]
 *     COLETAR --> VERIFICAR_AGENDA
 *
 *     VERIFICAR_AGENDA["🗓️ Verificar disponibilidade\nno Google Agenda do profissional\n(nó reutilizado por agendamento e reagendamento)"]
 *     VERIFICAR_AGENDA --> DISPONIVEL
 *
 *     DISPONIVEL{Há horário disponível?}
 *     DISPONIVEL -- SIM --> SUGERIR
 *     DISPONIVEL -- NÃO --> SEM_HORARIO
 *
 *     SEM_HORARIO["❌ Informar indisponibilidade\ne sugerir novo contato em outro momento"]
 *     SEM_HORARIO --> ENCERRAR_SEM
 *     ENCERRAR_SEM([🔴 ENCERRAR])
 *
 *     SUGERIR["📅 Apresentar horários disponíveis\npara o cliente escolher"]
 *     SUGERIR --> CONFIRMACAO
 *
 *     CONFIRMACAO{Cliente confirma o horário?}
 *     CONFIRMACAO -- SIM --> MARCAR
 *     CONFIRMACAO -- "NÃO (quer outro slot)" --> SUGERIR
 *
 *     MARCAR["✅ Registrar no Google Agenda do profissional\n+ Enviar confirmação ao cliente via WhatsApp"]
 *     MARCAR --> DECISAO_POS
 *
 *     DECISAO_POS{Ação pós-agendamento?}
 *     DECISAO_POS -- CONFIRMAR --> ANTIFRAUD
 *     DECISAO_POS -- DESMARCAR --> REMOVER_AGENDA
 *     DECISAO_POS -- REAGENDAR --> NOVO_HORARIO
 *
 *     ANTIFRAUD["🔒 Verificação anti-fraude\n+ Confirmação enviada ao cliente\n+ Notificação disparada ao profissional"]
 *     ANTIFRAUD --> ENCERRAR_CONFIRM
 *     ENCERRAR_CONFIRM([🟢 ENCERRAR])
 *
 *     REMOVER_AGENDA["🗑️ Remover evento do Google Agenda\n(sem aprovação do profissional)"]
 *     REMOVER_AGENDA --> ENCERRAR_CANCEL
 *     ENCERRAR_CANCEL([🔴 ENCERRAR])
 *
 *     NOVO_HORARIO["🔄 Solicitar apenas novo dia e horário\n(nome, contato e pagamento são mantidos,\nnão são solicitados novamente)"]
 *     NOVO_HORARIO --> VERIFICAR_AGENDA
 *
 * -----------------------------------------------------------------
 */

namespace Agendamento.Flow;

// ---------------------------------------------------------------------------
//  Enumerações
// ---------------------------------------------------------------------------

/// <summary>Identificadores únicos de cada nó do fluxo.</summary>
public enum NodeId
{
    Inicio,
    Acesso,
    Identificacao,
    DecisaoAgendar,
    Guardrail,
    EncerrarNao,
    Coletar,
    VerificarAgenda,        // Reutilizado por AgendamentoInicial e Reagendamento
    Disponivel,
    SemHorario,
    EncerrarSemHorario,
    Sugerir,
    Confirmacao,
    Marcar,
    DecisaoPosAgendamento,
    Antifraud,
    EncerrarConfirmado,
    RemoverAgenda,
    EncerrarCancelado,
    NovoHorario,            // Re-entry point do reagendamento
}

/// <summary>Tipo estrutural do nó, para fins de renderização e lógica.</summary>
public enum NodeType
{
    /// <summary>Ponto de início ou fim do fluxo.</summary>
    Terminal,
    /// <summary>Etapa de processamento linear.</summary>
    Process,
    /// <summary>Bifurcação com duas ou mais saídas condicionais.</summary>
    Decision,
}

// ---------------------------------------------------------------------------
//  Modelos
// ---------------------------------------------------------------------------

/// <summary>
/// Representa um nó do fluxo de agendamento.
/// </summary>
/// <param name="Id">Identificador único do nó.</param>
/// <param name="Type">Tipo estrutural (Terminal, Process, Decision).</param>
/// <param name="Label">Descrição curta exibida no diagrama.</param>
/// <param name="Notes">Observações de implementação (opcional).</param>
public sealed record FlowNode(
    NodeId Id,
    NodeType Type,
    string Label,
    string? Notes = null
);

/// <summary>
/// Representa uma transição direcional entre dois nós.
/// </summary>
/// <param name="From">Nó de origem.</param>
/// <param name="To">Nó de destino.</param>
/// <param name="Label">Condição ou rótulo da transição (ex: "SIM", "NÃO", "CONFIRMAR").</param>
/// <param name="IsLoopback">
///     Indica se a transição é um retorno (loop) a um nó anterior.
///     Útil para destacar visualmente e evitar ciclos infinitos na lógica.
/// </param>
public sealed record FlowEdge(
    NodeId From,
    NodeId To,
    string Label = "",
    bool IsLoopback = false
);

// ---------------------------------------------------------------------------
//  Definição do grafo
// ---------------------------------------------------------------------------

/// <summary>
/// Grafo completo do fluxo de agendamento via WhatsApp.
/// Referência canônica para implementação no Antigravity.
/// </summary>
public static class AgendamentoFlowGraph
{
    public static IReadOnlyList<FlowNode> Nodes { get; } = new[]
    {
        // ── Entrada ──────────────────────────────────────────────────────────
        new FlowNode(NodeId.Inicio,
            NodeType.Terminal,
            "INÍCIO",
            "Cliente chega via link direto ou QR Code no WhatsApp."),

        new FlowNode(NodeId.Acesso,
            NodeType.Process,
            "Acesso ao agente via WhatsApp",
            "Bot recebe a mensagem de abertura e inicia o atendimento automaticamente."),

        new FlowNode(NodeId.Identificacao,
            NodeType.Process,
            "Identificação e intenção",
            "Agente cumprimenta, faz identificação básica e pergunta se o cliente deseja agendar."),

        new FlowNode(NodeId.DecisaoAgendar,
            NodeType.Decision,
            "Deseja agendar?"),

        // ── Ramo NÃO ─────────────────────────────────────────────────────────
        new FlowNode(NodeId.Guardrail,
            NodeType.Process,
            "Oferecer ajuda (guardrail)",
            "Bot pergunta se pode ajudar com algo simples. " +
            "Guardrail ativo: escopo restrito a agendamento e atendimento básico. " +
            "Sem conversas abertas ou desvios de tema."),

        new FlowNode(NodeId.EncerrarNao,
            NodeType.Terminal,
            "ENCERRAR",
            "Bot agradece e encerra cordialmente."),

        // ── Ramo SIM ─────────────────────────────────────────────────────────
        new FlowNode(NodeId.Coletar,
            NodeType.Process,
            "Coletar dados do cliente",
            "Coleta: nome completo, dias preferidos, horário preferido, " +
            "pelo menos 1 contato (celular ou e-mail) e forma de pagamento. " +
            "IMPORTANTE: dados persistidos — não serão solicitados novamente em reagendamentos."),

        // ── Verificação de agenda (compartilhada) ────────────────────────────
        new FlowNode(NodeId.VerificarAgenda,
            NodeType.Process,
            "Verificar disponibilidade no Google Agenda",
            "Consulta a API do Google Agenda do profissional. " +
            "NÓ COMPARTILHADO: utilizado tanto no agendamento inicial quanto no reagendamento."),

        new FlowNode(NodeId.Disponivel,
            NodeType.Decision,
            "Há horário disponível?"),

        new FlowNode(NodeId.SemHorario,
            NodeType.Process,
            "Informar indisponibilidade",
            "Bot informa que não há slots nos dias/horário solicitados " +
            "e sugere que o cliente entre em contato em outro momento."),

        new FlowNode(NodeId.EncerrarSemHorario,
            NodeType.Terminal,
            "ENCERRAR"),

        // ── Confirmação de slot ──────────────────────────────────────────────
        new FlowNode(NodeId.Sugerir,
            NodeType.Process,
            "Sugerir horários disponíveis",
            "Bot apresenta os slots disponíveis para o cliente escolher."),

        new FlowNode(NodeId.Confirmacao,
            NodeType.Decision,
            "Cliente confirma o horário?"),

        new FlowNode(NodeId.Marcar,
            NodeType.Process,
            "Registrar agendamento",
            "Registra o evento no Google Agenda do profissional e " +
            "envia confirmação ao cliente via WhatsApp."),

        // ── Pós-agendamento ──────────────────────────────────────────────────
        new FlowNode(NodeId.DecisaoPosAgendamento,
            NodeType.Decision,
            "Ação pós-agendamento?",
            "Disponível após o agendamento ser registrado. " +
            "Três saídas: CONFIRMAR, DESMARCAR ou REAGENDAR."),

        new FlowNode(NodeId.Antifraud,
            NodeType.Process,
            "Anti-fraude + Notificações (CONFIRMAR)",
            "Verificação anti-fraude executada. " +
            "Confirmação enviada ao cliente e notificação disparada ao profissional."),

        new FlowNode(NodeId.EncerrarConfirmado,
            NodeType.Terminal,
            "ENCERRAR"),

        new FlowNode(NodeId.RemoverAgenda,
            NodeType.Process,
            "Remover evento do Google Agenda (DESMARCAR)",
            "Desmarcação direta: evento removido automaticamente do Google Agenda " +
            "sem necessidade de aprovação do profissional."),

        new FlowNode(NodeId.EncerrarCancelado,
            NodeType.Terminal,
            "ENCERRAR"),

        // ── Re-entry do reagendamento ────────────────────────────────────────
        new FlowNode(NodeId.NovoHorario,
            NodeType.Process,
            "Solicitar novo dia e horário (REAGENDAR)",
            "RE-ENTRY POINT: bot solicita apenas o novo dia e horário desejado. " +
            "Nome, contato e forma de pagamento são mantidos dos dados salvos — " +
            "sem reinserção redundante. Fluxo retoma em VerificarAgenda."),
    };

    public static IReadOnlyList<FlowEdge> Edges { get; } = new[]
    {
        // Entrada
        new FlowEdge(NodeId.Inicio,               NodeId.Acesso),
        new FlowEdge(NodeId.Acesso,               NodeId.Identificacao),
        new FlowEdge(NodeId.Identificacao,         NodeId.DecisaoAgendar),

        // Decisão inicial
        new FlowEdge(NodeId.DecisaoAgendar,        NodeId.Coletar,                  "SIM"),
        new FlowEdge(NodeId.DecisaoAgendar,        NodeId.Guardrail,                "NÃO"),
        new FlowEdge(NodeId.Guardrail,             NodeId.EncerrarNao),

        // Coleta → verificação
        new FlowEdge(NodeId.Coletar,               NodeId.VerificarAgenda),

        // Verificação de disponibilidade
        new FlowEdge(NodeId.VerificarAgenda,       NodeId.Disponivel),
        new FlowEdge(NodeId.Disponivel,            NodeId.Sugerir,                  "SIM"),
        new FlowEdge(NodeId.Disponivel,            NodeId.SemHorario,               "NÃO"),
        new FlowEdge(NodeId.SemHorario,            NodeId.EncerrarSemHorario),

        // Confirmação de slot
        new FlowEdge(NodeId.Sugerir,               NodeId.Confirmacao),
        new FlowEdge(NodeId.Confirmacao,           NodeId.Marcar,                   "SIM"),
        new FlowEdge(NodeId.Confirmacao,           NodeId.Sugerir,                  "NÃO — quer outro slot",  IsLoopback: true),

        // Pós-agendamento
        new FlowEdge(NodeId.Marcar,                NodeId.DecisaoPosAgendamento),
        new FlowEdge(NodeId.DecisaoPosAgendamento, NodeId.Antifraud,                "CONFIRMAR"),
        new FlowEdge(NodeId.DecisaoPosAgendamento, NodeId.RemoverAgenda,            "DESMARCAR"),
        new FlowEdge(NodeId.DecisaoPosAgendamento, NodeId.NovoHorario,              "REAGENDAR"),

        // Saídas do pós-agendamento
        new FlowEdge(NodeId.Antifraud,             NodeId.EncerrarConfirmado),
        new FlowEdge(NodeId.RemoverAgenda,         NodeId.EncerrarCancelado),

        // Loop de reagendamento → volta para verificação (dados mantidos)
        new FlowEdge(NodeId.NovoHorario,           NodeId.VerificarAgenda,          "Retomar com dados salvos", IsLoopback: true),
    };
}