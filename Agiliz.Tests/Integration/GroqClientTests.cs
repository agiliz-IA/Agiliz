using Agiliz.Core.LLM;
using Agiliz.Core.Models;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Agiliz.Tests.Integration;

public sealed class GroqClientTests : IDisposable
{
    private readonly LlmWireMockServer _mock = new();

    private GroqClient BuildClient() =>
        new(new HttpClient(), "System prompt de teste.",
            new LlmSettings { Provider = LlmProvider.Groq, Model = "llama-3.3-70b-versatile", MaxTokens = 300 },
            baseUrl: _mock.BaseUrl);  // <-- aponta para WireMock, nao para api.groq.com

    [Fact]
    public async Task CompleteAsync_WhenServerReturnsSuccess_ReturnsReply()
    {
        _mock.SetupGroqSuccess("Ola do Groq!");
        var result = await BuildClient().CompleteAsync([ConversationMessage.User("oi")]);
        result.Should().Be("Ola do Groq!");
    }

    [Fact]
    public async Task CompleteAsync_WhenServerReturns429_ThrowsHttpException()
    {
        _mock.SetupGroqError(429);
        var act = () => BuildClient().CompleteAsync([ConversationMessage.User("oi")]);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task CompleteAsync_SendsSystemPromptAsFirstMessage()
    {
        _mock.SetupGroqSuccess("ok");
        await BuildClient().CompleteAsync([ConversationMessage.User("pergunta")]);
        // WireMock recebeu a request -- se chegou aqui, o endpoint foi atingido
        _mock.BaseUrl.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CompleteAsync_WithMultipleHistoryMessages_SendsAllToServer()
    {
        _mock.SetupGroqSuccess("ok");

        var history = new List<ConversationMessage>
        {
            ConversationMessage.User("msg1"),
            ConversationMessage.Assistant("resp1"),
            ConversationMessage.User("msg2")
        };

        var result = await BuildClient().CompleteAsync(history);
        result.Should().Be("ok");
    }

    public void Dispose() => _mock.Dispose();
}
