using Agiliz.Core.Models;
using Agiliz.Runtime.Services;
using Agiliz.Tests.Fakes;
using Agiliz.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class BotRunnerTests
{
    private readonly FakeLlmClient _llm = new();
    private readonly SessionStore _sessions = new();
    private readonly BotRunner _runner;

    public BotRunnerTests()
    {
        var mockConfig = new Moq.Mock<Microsoft.Extensions.Configuration.IConfiguration>();
        mockConfig.Setup(c => c["ConfigsDir"]).Returns((string?)null);
        _runner = new BotRunner(_sessions, NullLogger<BotRunner>.Instance, Array.Empty<Agiliz.Core.Tools.ITool>(), mockConfig.Object);
    }

    private TenantEntry Tenant(BotConfig config) => new(config, _llm);

    // ─── Flow matching ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_WhenMessageMatchesTrigger_ReturnFlowResponseWithoutCallingLlm()
    {
        var config = TestFixtures.DefaultConfig()
            .WithFlows(("cardápio", "Nosso cardápio é pizza e lasanha."));

        var result = await _runner.ProcessAsync(Tenant(config), "+5521999", "quero ver o cardápio");

        result.Should().Be("Nosso cardápio é pizza e lasanha.");
        _llm.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ProcessAsync_FlowMatch_IsCaseInsensitive()
    {
        var config = TestFixtures.DefaultConfig()
            .WithFlows(("endereço", "Estamos na Rua X."));

        var result = await _runner.ProcessAsync(Tenant(config), "+5521999", "ENDEREÇO");

        result.Should().Be("Estamos na Rua X.");
    }

    [Fact]
    public async Task ProcessAsync_FlowMatch_MatchesSubstring()
    {
        var config = TestFixtures.DefaultConfig()
            .WithFlows(("horario", "Abrimos às 11h."));

        var result = await _runner.ProcessAsync(Tenant(config), "+5521999", "qual é o horario de funcionamento?");

        result.Should().Be("Abrimos às 11h.");
    }

    [Fact]
    public async Task ProcessAsync_WhenNoFlowMatches_CallsLlm()
    {
        var config = TestFixtures.DefaultConfig()
            .WithFlows(("cardápio", "Pizza e lasanha."));

        _llm.Returns("Temos promoção hoje!");

        var result = await _runner.ProcessAsync(Tenant(config), "+5521999", "vocês têm promoção?");

        result.Should().Be("Temos promoção hoje!");
        _llm.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ProcessAsync_WhenNoFlows_AlwaysCallsLlm()
    {
        var config = TestFixtures.DefaultConfig();
        _llm.Returns("Olá, posso ajudar?");

        await _runner.ProcessAsync(Tenant(config), "+5521999", "oi");

        _llm.CallCount.Should().Be(1);
    }

    // ─── Histórico passado ao LLM ─────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_PassesUserMessageInHistoryToLlm()
    {
        var config = TestFixtures.DefaultConfig();
        _llm.Returns("Resposta.");

        await _runner.ProcessAsync(Tenant(config), "+5521999", "minha pergunta");

        var receivedHistory = _llm.ReceivedHistories[0];
        receivedHistory.Should().ContainSingle(m =>
            m.Role == MessageRole.User && m.Content == "minha pergunta");
    }

    [Fact]
    public async Task ProcessAsync_AccumulatesHistoryAcrossCalls()
    {
        var config = TestFixtures.DefaultConfig();
        _llm.Returns("Primeira resposta.", "Segunda resposta.");

        await _runner.ProcessAsync(Tenant(config), "+5521999", "mensagem 1");
        await _runner.ProcessAsync(Tenant(config), "+5521999", "mensagem 2");

        var lastHistory = _llm.ReceivedHistories[1];
        lastHistory.Should().HaveCount(4); // user1, assistant1, user2
        lastHistory[0].Content.Should().Be("mensagem 1");
        lastHistory[1].Role.Should().Be(MessageRole.Assistant);
        lastHistory[2].Content.Should().Be("mensagem 2");
    }

    [Fact]
    public async Task ProcessAsync_DifferentPhones_HaveIsolatedHistories()
    {
        var config = TestFixtures.DefaultConfig();
        _llm.Returns("r1", "r2");

        await _runner.ProcessAsync(Tenant(config), "+5521111", "msg de alice");
        await _runner.ProcessAsync(Tenant(config), "+5522222", "msg de bob");

        // Bob não vê o histórico da Alice
        var bobHistory = _llm.ReceivedHistories[1];
        bobHistory.Should().ContainSingle(m => m.Content == "msg de bob");
    }

    // ─── Fallback de erro ─────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_WhenLlmThrows_ReturnsFallbackMessage()
    {
        var config = TestFixtures.DefaultConfig();
        _llm.Throws(new HttpRequestException("timeout"));

        var result = await _runner.ProcessAsync(Tenant(config), "+5521999", "oi");

        result.Should().Contain("problema técnico");
    }

    [Fact]
    public async Task ProcessAsync_WhenLlmThrows_DoesNotAddFailedReplyToHistory()
    {
        var config = TestFixtures.DefaultConfig();
        _llm.Throws(new HttpRequestException("timeout"))
            .Returns("Recuperado.");

        await _runner.ProcessAsync(Tenant(config), "+5521999", "msg1");
        var result = await _runner.ProcessAsync(Tenant(config), "+5521999", "msg2");

        // Apenas msg1 e msg2 no histórico, sem lixo do erro
        var lastHistory = _llm.ReceivedHistories[1];
        lastHistory.Should().HaveCount(3);
        result.Should().Be("Recuperado.");
    }
}
