using Agiliz.Core.LLM;
using Agiliz.Core.Models;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace Agiliz.Tests.Integration;

public sealed class ClaudeClientTests : IDisposable
{
    private readonly LlmWireMockServer _mock = new();

    private ClaudeClient BuildClient()
    {
        var http = new HttpClient { BaseAddress = new Uri(_mock.BaseUrl) };
        http.DefaultRequestHeaders.Add("x-api-key", "fake-key");
        http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");

        var settings = new LlmSettings
        {
            Provider = LlmProvider.Claude,
            Model = "claude-sonnet-4-20250514",
            MaxTokens = 300
        };

        return new ClaudeClient(http, "System prompt de teste.", settings);
    }

    [Fact]
    public async Task CompleteAsync_WhenServerReturnsSuccess_ReturnsTextContent()
    {
        _mock.SetupClaudeSuccess("Olá do Claude!");

        var result = await BuildClient().CompleteAsync([ConversationMessage.User("oi")]);

        result.Should().Be("Olá do Claude!");
    }

    [Fact]
    public async Task CompleteAsync_WhenServerReturns529_ThrowsHttpException()
    {
        _mock.SetupClaudeError(529);

        var act = () => BuildClient().CompleteAsync([ConversationMessage.User("oi")]);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task CompleteAsync_WhenServerReturns401_ThrowsHttpException()
    {
        _mock.SetupClaudeError(401);

        var act = () => BuildClient().CompleteAsync([ConversationMessage.User("oi")]);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    public void Dispose() => _mock.Dispose();
}
