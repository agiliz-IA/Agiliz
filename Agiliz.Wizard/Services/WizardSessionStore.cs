using System.Collections.Concurrent;
using Agiliz.Core.LLM;
using Agiliz.Core.Models;

namespace Agiliz.Wizard.Services;

public sealed class WizardSession
{
    public Guid Id { get; } = Guid.NewGuid();
    public required ILlmClient LlmClient { get; init; }
    public BotType Type { get; init; } = BotType.Generic;
    public List<ConversationMessage> History { get; } = [];
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WizardSessionStore
{
    private readonly string _configsDir;

    public WizardSessionStore(Microsoft.Extensions.Configuration.IConfiguration config)
    {
        _configsDir = config["ConfigsDir"] ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "configs"));
    }

    private const string MetaSystemPrompt = """
        Você é o Agiliz, um assistente especialista em criação de bots de WhatsApp.
        Sua tarefa é entrevistar o usuário para coletar informações sobre o negócio
        e gerar a configuração completa de um bot de atendimento.

        FASE DE ENTREVISTA
        Faça perguntas objetivas e uma por vez. Colete:
        1. Nome e ramo do negócio
        2. Qual o tom do atendimento (formal, descontraído, técnico...)
        3. Principais dúvidas/pedidos que os clientes fazem (para os fluxos FAQ)
        4. Horário de funcionamento (se relevante)
        5. Informações importantes que o bot deve sempre saber (endereço, preços, etc.)

        Quando você tiver informações suficientes para gerar o config, diga:
        "Tenho tudo que preciso. Gerando configuração..."

        FASE DE GERAÇÃO
        Emita EXATAMENTE este bloco (e nada mais após ele):

        ===JSON_START===
        {
          "systemPrompt": "<prompt completo do bot em português>",
          "flows": [
            { "trigger": "<palavra-chave>", "response": "<resposta direta>" }
          ]
        }
        ===JSON_END===

        REGRAS para o systemPrompt:
        - Escreva na segunda pessoa do singular, como se o bot fosse uma pessoa real
        - Inclua nome, cargo/papel e personalidade
        - Instrua o bot a ser conciso (respostas curtas para WhatsApp)
        - Inclua as informações coletadas na entrevista
        - Termine com: "Nunca invente informações. Se não souber, peça para o cliente aguardar."

        REGRAS para os flows:
        - Máximo 8 entradas. Apenas para respostas que NÃO precisam de raciocínio.
        - Triggers em letras minúsculas, sem acento, uma palavra ou expressão curta.
        - Respostas diretas e completas (serão enviadas sem passar pelo LLM).
        """;

    private const string SchedulingMetaSystemPrompt = """
        Você é o Agiliz, um assistente especialista em criação de bots de WhatsApp de Agendamento.
        Sua tarefa é entrevistar o profissional para configurar um bot de marcação de horários.

        FASE DE ENTREVISTA
        Faça perguntas objetivas e uma por vez. Colete:
        1. Nome do profissional ou negócio.
        2. Tom do atendimento (formal, simpático...).
        3. Quais são os horários e dias de funcionamento.
        4. O bot precisa pedir consentimento para salvar dados (LGPD)?
        5. Quais as formas de pagamento aceitas (Dinheiro, Cartão, Plano de Saúde, etc)?
        6. Qual o tempo médio de cada agendamento?

        Quando tiver essas informações, diga: "Tenho tudo que preciso. Gerando configuração..."

        FASE DE GERAÇÃO
        Emita EXATAMENTE este bloco (e nada mais após ele):

        ===JSON_START===
        {
          "type": 1,
          "systemPrompt": "<prompt completo do bot em português, com restrições severas para focar apenas em agendamento, instrução para chamar a tool verificar_agenda e marcar_agenda, e fluxo de coleta de nome/contato/forma_de_pagamento/consentimento_lgpd>",
          "lgpdConsentRequired": <true/false>,
          "paymentMethods": ["Dinheiro", "Cartão"],
          "flows": [
            { "trigger": "CONFIRMAR_AGENDAMENTO", "response": "Seu agendamento foi confirmado com sucesso!" },
            { "trigger": "DESMARCAR_AGENDAMENTO", "response": "Agendamento cancelado. Até a próxima." },
            { "trigger": "REAGENDAR", "response": "Vamos escolher um novo horário." }
          ]
        }
        ===JSON_END===

        REGRAS para o systemPrompt do bot final:
        - Instrua o bot a NUNCA fugir do escopo de agendamento (guardrail).
        - Se o usuário tentar puxar outro assunto, o bot deve perguntar educadamente se pode ajudar com algo simples de agendamento.
        - O bot deve coletar os dados na ordem, sempre perguntando um de cada vez.
        """;

    private readonly ConcurrentDictionary<Guid, WizardSession> _sessions = new();

    public WizardSession Create(BotType botType = BotType.Generic, string? editContext = null)
    {
        var metaConfig = new BotConfig
        {
            SystemPrompt = botType == BotType.Scheduling ? SchedulingMetaSystemPrompt : MetaSystemPrompt,
            Llm = new LlmSettings
            {
                Provider = LlmProvider.Groq,
                Model = "llama-3.3-70b-versatile",
                MaxTokens = 2000
            }
        };

        var session = new WizardSession { LlmClient = LlmClientFactory.Create(metaConfig, new HttpClient()), Type = botType };

        if (editContext is not null)
            session.History.Add(ConversationMessage.User(editContext));

        _sessions[session.Id] = session;
        return session;
    }

    public WizardSession? Get(Guid id) =>
        _sessions.TryGetValue(id, out var s) ? s : null;

    public async Task<MessageResult> SendAsync(Guid id, string message, CancellationToken ct = default)
    {
        var session = Get(id) ?? throw new KeyNotFoundException($"Sessão {id} não encontrada.");

        session.History.Add(ConversationMessage.User(message));
        session.LastActivity = DateTimeOffset.UtcNow;

        var response = await session.LlmClient.CompleteAsync(session.History, null, ct);
        var reply = response.Text;
        session.History.Add(ConversationMessage.Assistant(reply));

        var tokenCostUsd = (response.Usage.Prompt * 0.000003m) + (response.Usage.Completion * 0.000015m);
        Agiliz.Core.Billing.BillingStore.Record(_configsDir, new CostEntry { TenantId = "painel-admin", Type = CostType.TokensLLM, Description = $"Wizard MetaAgent ({response.Usage.Total} tok)", AmountUsd = tokenCostUsd });

        var configReady = reply.Contains("===JSON_START===") && reply.Contains("===JSON_END===");
        string? jsonConfig = null;

        if (configReady)
        {
            var start = reply.IndexOf("===JSON_START===", StringComparison.Ordinal) + "===JSON_START===".Length;
            var end = reply.IndexOf("===JSON_END===", StringComparison.Ordinal);
            jsonConfig = reply[start..end].Trim();
        }

        // Remove o bloco JSON do texto exibido ao usuário
        var displayReply = configReady
            ? reply[..reply.IndexOf("===JSON_START===", StringComparison.Ordinal)].Trim()
            : reply;

        return new MessageResult(displayReply, configReady, jsonConfig);
    }

    public void Remove(Guid id) => _sessions.TryRemove(id, out _);

    public void PurgeExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        foreach (var key in _sessions.Keys)
            if (_sessions.TryGetValue(key, out var s) && s.LastActivity < cutoff)
                _sessions.TryRemove(key, out _);
    }
}

public sealed record MessageResult(string Reply, bool ConfigReady, string? JsonConfig);
