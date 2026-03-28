using System.Collections.Concurrent;
using Agiliz.Core.LLM;
using Agiliz.Core.Models;

namespace Agiliz.Wizard.Services;

public sealed class WizardSession
{
    public Guid Id { get; } = Guid.NewGuid();
    public required ILlmClient LlmClient { get; init; }
    public List<ConversationMessage> History { get; } = [];
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class WizardSessionStore
{
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

    private readonly ConcurrentDictionary<Guid, WizardSession> _sessions = new();

    public WizardSession Create(string? editContext = null)
    {
        var metaConfig = new BotConfig
        {
            SystemPrompt = MetaSystemPrompt,
            Llm = new LlmSettings
            {
                Provider = LlmProvider.Groq,
                Model = "llama-3.3-70b-versatile",
                MaxTokens = 2000
            }
        };

        var session = new WizardSession { LlmClient = LlmClientFactory.Create(metaConfig, new HttpClient()) };

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

        var reply = await session.LlmClient.CompleteAsync(session.History, null, ct);
        session.History.Add(ConversationMessage.Assistant(reply));

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
