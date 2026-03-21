using Agiliz.Core.Models;

namespace Agiliz.Tests.Helpers;

/// <summary>
/// Fábrica de objetos de teste com valores padrão sensatos.
/// Use os métodos With* para customizar apenas o que importa no teste.
/// </summary>
public static class TestFixtures
{
    public static BotConfig DefaultConfig(
        string tenantId = "test-bot",
        string twilioNumber = "whatsapp:+15005550006",
        LlmProvider provider = LlmProvider.Groq,
        string? systemPrompt = null,
        List<FlowEntry>? flows = null) => new()
    {
        TenantId = tenantId,
        TwilioNumber = twilioNumber,
        SystemPrompt = systemPrompt ?? "Você é um assistente de testes.",
        Flows = flows ?? [],
        Llm = new LlmSettings
        {
            Provider = provider,
            Model = "llama-3.3-70b-versatile",
            MaxTokens = 300
        }
    };

    public static BotConfig WithFlows(this BotConfig config, params (string trigger, string response)[] flows)
    {
        var list = flows.Select(f => new FlowEntry { Trigger = f.trigger, Response = f.response }).ToList();
        return config with { Flows = list };
    }

    public static FlowEntry Flow(string trigger, string response) =>
        new() { Trigger = trigger, Response = response };

    /// <summary>
    /// Bloco JSON que o meta-agente emite ao fim da entrevista.
    /// Usado para testar ExtractConfig sem chamar o LLM.
    /// </summary>
    public static string AgentConfigReply(
        string systemPrompt = "Você é a Lia.",
        List<(string trigger, string response)>? flows = null)
    {
        var flowJson = flows is null ? "[]" : string.Join(",\n",
            flows.Select(f => $$"""{"trigger":"{{f.trigger}}","response":"{{f.response}}"}"""));

        return $"""
            Tenho tudo que preciso. Gerando configuração...

            ===JSON_START===
            {{{{
              "systemPrompt": "{systemPrompt}",
              "flows": [{flowJson}]
            }}}}
            ===JSON_END===
            """;
    }

    public static string AgentConfigReplyFormatted(
        string systemPrompt = "Você é a Lia.",
        string flowsJson = "[]") =>
        $"""
        Tenho tudo que preciso. Gerando configuração...

        ===JSON_START===
        {{
          "systemPrompt": "{systemPrompt}",
          "flows": {flowsJson}
        }}
        ===JSON_END===
        """;

    /// <summary>Cria uma pasta temporária que é deletada ao fim do teste.</summary>
    public static TempDirectory CreateTempDir() => new();
}

public sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"agiliz-test-{Guid.NewGuid():N}");

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        if (Directory.Exists(Path))
            Directory.Delete(Path, recursive: true);
    }
}
