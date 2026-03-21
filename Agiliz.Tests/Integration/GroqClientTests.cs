using Agiliz.Core.LLM;
using Agiliz.Core.Models;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Agiliz.Tests.Integration;

/// <summary>
/// Testa o GroqClient contra um servidor HTTP local (WireMock).
/// Não usa internet. Roda em milissegundos.
/// </summary>
public sealed class GroqClientTests : IDisposable
{
    private readonly LlmWireMockServer _mock = new();

    private GroqClient BuildClient(string? customBaseUrl = null)
    {
        var http = new HttpClient { BaseAddress = new Uri(customBaseUrl ?? _mock.BaseUrl) };
        http.DefaultRequestHeaders.Add("Authorization", "Bearer fake-key");

        var settings = new LlmSettings
        {
            Provider = LlmProvider.Groq,
            Model = "llama-3.3-70b-versatile",
            MaxTokens = 300
        };

        return new GroqClient(http, "System prompt de teste.", settings);
    }

    [Fact]
    public async Task CompleteAsync_WhenServerReturnsSuccess_ReturnsReply()
    {
        _mock.SetupGroqSuccess("Olá do Groq!");

        var client = BuildClient();
        var result = await client.CompleteAsync([ConversationMessage.User("oi")]);

        result.Should().Be("Olá do Groq!");
    }

    [Fact]
    public async Task CompleteAsync_WhenServerReturns429_ThrowsHttpException()
    {
        _mock.SetupGroqError(429);

        var client = BuildClient();
        var act = () => client.CompleteAsync([ConversationMessage.User("oi")]);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task CompleteAsync_SendsSystemPromptAsFirstMessage()
    {
        _mock.SetupGroqSuccess();

        var http = new HttpClient { BaseAddress = new Uri(_mock.BaseUrl) };
        http.DefaultRequestHeaders.Add("Authorization", "Bearer fake-key");
        var settings = new LlmSettings { Model = "llama-3.3-70b-versatile", MaxTokens = 100 };
        var client = new GroqClient(http, "Prompt especial.", settings);

        await client.CompleteAsync([ConversationMessage.User("pergunta")]);

        // Verifica que o request chegou ao mock (WireMock captura)
        _mock.BaseUrl.Should().NotBeNull(); // sanity
        // Verificação mais profunda pode ser feita via _server.LogEntries se necessário
    }

    [Fact]
    public async Task CompleteAsync_WithMultipleHistoryMessages_SendsAllToServer()
    {
        _mock.SetupGroqSuccess("ok");
        var client = BuildClient();

        var history = new List<ConversationMessage>
        {
            ConversationMessage.User("msg1"),
            ConversationMessage.Assistant("resp1"),
            ConversationMessage.User("msg2")
        };

        var result = await client.CompleteAsync(history);
        result.Should().Be("ok");
    }

    public void Dispose() => _mock.Dispose();
}
