using System.Text.Json;
using Agiliz.Core.LLM;
using Agiliz.Core.Models;

namespace Agiliz.CLI.Agent;

/// <summary>
/// Conduz a entrevista interativa com o criador do bot.
/// Usa o LLM com contexto completo (sem limite de tokens reduzido).
/// Ao coletar informações suficientes, solicita ao LLM que emita um
/// bloco JSON delimitado por ===JSON_START=== / ===JSON_END===.
/// </summary>
public sealed class MetaAgentSession
{
    private const string SystemPrompt = """
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

    private readonly ILlmClient _llm;
    private readonly List<ConversationMessage> _history = [];

    public MetaAgentSession()
    {
        // Meta-agente usa MaxTokens generoso — é uma operação única de configuração
        var metaConfig = new BotConfig
        {
            SystemPrompt = SystemPrompt,
            Llm = new LlmSettings
            {
                Provider = LlmProvider.Groq,
                Model = "llama-3.3-70b-versatile",
                MaxTokens = 2000
            }
        };

        _llm = LlmClientFactory.Create(metaConfig);
    }

    /// <summary>
    /// Envia a mensagem do usuário e retorna a resposta do agente.
    /// Retorna null quando o JSON de config já foi emitido e a sessão está encerrada.
    /// </summary>
    public async Task<string> SendAsync(string userMessage, CancellationToken ct = default)
    {
        _history.Add(ConversationMessage.User(userMessage));

        var reply = await _llm.CompleteAsync(_history, ct);
        _history.Add(ConversationMessage.Assistant(reply));

        return reply;
    }

    /// <summary>Verifica se a resposta contém o bloco de config gerado.</summary>
    public static bool HasConfigBlock(string reply) =>
        reply.Contains("===JSON_START===") && reply.Contains("===JSON_END===");

    /// <summary>
    /// Extrai e desserializa o bloco JSON emitido pelo agente.
    /// Retorna (systemPrompt, flows).
    /// </summary>
    public static (string SystemPrompt, List<FlowEntry> Flows) ExtractConfig(string reply)
    {
        var start = reply.IndexOf("===JSON_START===", StringComparison.Ordinal) + "===JSON_START===".Length;
        var end = reply.IndexOf("===JSON_END===", StringComparison.Ordinal);
        var json = reply[start..end].Trim();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var systemPrompt = root.GetProperty("systemPrompt").GetString()
                           ?? throw new InvalidOperationException("systemPrompt ausente no JSON gerado.");

        var flows = new List<FlowEntry>();
        if (root.TryGetProperty("flows", out var flowsEl))
        {
            foreach (var f in flowsEl.EnumerateArray())
            {
                flows.Add(new FlowEntry
                {
                    Trigger = f.GetProperty("trigger").GetString() ?? "",
                    Response = f.GetProperty("response").GetString() ?? ""
                });
            }
        }

        return (systemPrompt, flows);
    }
}
