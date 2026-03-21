using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Agiliz.Tests.Helpers;

/// <summary>
/// Servidor HTTP local (WireMock) que simula respostas do Groq e do Claude.
/// Permite testar GroqClient e ClaudeClient sem internet e sem gastar tokens.
/// </summary>
public sealed class LlmWireMockServer : IDisposable
{
    private readonly WireMockServer _server;

    public string BaseUrl => _server.Url!;

    public LlmWireMockServer()
    {
        _server = WireMockServer.Start();
    }

    // ─── Groq ─────────────────────────────────────────────────────────────────

    public LlmWireMockServer SetupGroqSuccess(string reply = "Resposta do Groq.")
    {
        _server
            .Given(Request.Create()
                .WithPath("/openai/v1/chat/completions")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                    {
                      "choices": [
                        { "message": { "content": "{{reply}}" } }
                      ]
                    }
                    """));
        return this;
    }

    public LlmWireMockServer SetupGroqError(int statusCode = 429, string message = "rate limit exceeded")
    {
        _server
            .Given(Request.Create()
                .WithPath("/openai/v1/chat/completions")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""{"error":{"message":"{{message}}"}}"""));
        return this;
    }

    /// <summary>
    /// Configura uma resposta de config do meta-agente (com bloco JSON delimitado).
    /// Útil para testar o fluxo completo do Wizard.
    /// </summary>
    public LlmWireMockServer SetupGroqMetaAgentConfig(
        string systemPrompt = "Você é a Lia.",
        string flows = """[{"trigger":"cardápio","response":"Pizza!"}]""")
    {
        var configBlock = $$"""
            Tenho tudo que preciso. Gerando configuração...

            ===JSON_START===
            {
              "systemPrompt": "{{systemPrompt}}",
              "flows": {{flows}}
            }
            ===JSON_END===
            """;

        _server
            .Given(Request.Create()
                .WithPath("/openai/v1/chat/completions")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                    {
                      "choices": [
                        { "message": { "content": "{{EscapeJson(configBlock)}}" } }
                      ]
                    }
                    """));
        return this;
    }

    // ─── Claude ───────────────────────────────────────────────────────────────

    public LlmWireMockServer SetupClaudeSuccess(string reply = "Resposta do Claude.")
    {
        _server
            .Given(Request.Create()
                .WithPath("/v1/messages")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody($$"""
                    {
                      "content": [
                        { "type": "text", "text": "{{reply}}" }
                      ]
                    }
                    """));
        return this;
    }

    public LlmWireMockServer SetupClaudeError(int statusCode = 529)
    {
        _server
            .Given(Request.Create()
                .WithPath("/v1/messages")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(statusCode)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"error":{"type":"overloaded_error"}}"""));
        return this;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    public void Reset() => _server.Reset();

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");

    public void Dispose() => _server.Stop();
}
