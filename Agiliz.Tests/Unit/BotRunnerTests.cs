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
    private readonly FakeCostRecorder _costs = new();
    private readonly BotRunner _runner;

    public BotRunnerTests()
    {
        _runner = new BotRunner(_sessions, NullLogger<BotRunner>.Instance, Array.Empty<Agiliz.Core.Tools.ITool>(), _costs);
    }

    // ─── Billing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_WhenLlmAnswers_RecordsTokenCostForTenant()
    {
        _llm.Returns("Olá!");

        await _runner.ProcessAsync(Tenant(TestFixtures.DefaultConfig(tenantId: "clinica")), "+5521999", "bom dia");

        var entry = _costs.Entries.Should().ContainSingle().Subject;
        entry.TenantId.Should().Be("clinica");
        entry.Type.Should().Be(CostType.TokensLLM);
        entry.AmountUsd.Should().Be(Agiliz.Core.LLM.ModelCatalog.EstimateCostUsd("openai/gpt-oss-120b", new TokenUsage(10, 20)));
    }

    [Fact]
    public async Task ProcessAsync_FlowMatch_RecordsNoCost()
    {
        var config = TestFixtures.DefaultConfig().WithFlows(("oi", "Olá!"));

        await _runner.ProcessAsync(Tenant(config), "+5521999", "oi");

        _costs.Entries.Should().BeEmpty();
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

        var lastHistory = Conversation(_llm.ReceivedHistories[1]);
        lastHistory.Should().HaveCount(3); // user1, assistant1, user2 (snapshot no momento da chamada)
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
        var lastHistory = Conversation(_llm.ReceivedHistories[1]);
        lastHistory.Should().HaveCount(2);
        result.Should().Be("Recuperado.");
    }

    // ─── Contexto de data/hora ────────────────────────────────────────────────

    [Fact]
    public async Task ProcessAsync_SendsTimeContextAsFirstSystemMessage()
    {
        var config = TestFixtures.DefaultConfig();
        _llm.Returns("ok");

        await _runner.ProcessAsync(Tenant(config), "+5521999", "quero marcar amanhã");

        var first = _llm.ReceivedHistories[0][0];
        first.Role.Should().Be(MessageRole.System);
        first.Content.Should().Contain("America/Sao_Paulo");
    }

    [Fact]
    public void BuildTimeContext_UsesBusinessTimeZone()
    {
        var config = TestFixtures.DefaultConfig();
        var utc = new DateTimeOffset(2026, 11, 20, 15, 30, 0, TimeSpan.Zero);

        var text = BotRunner.BuildTimeContext(config, utc);

        text.Should().Contain("20/11/2026 às 12:30");
        text.Should().Contain("UTC-03:00");
        text.Should().Contain("2026-11-20T14:00:00-03:00");
    }

    [Fact]
    public async Task ProcessAsync_WhenLlmReturnsEmpty_ReturnsGentleFallback()
    {
        var config = TestFixtures.DefaultConfig();
        _llm.Returns("   ");

        var result = await _runner.ProcessAsync(Tenant(config), "+5521999", "oi?");

        result.Should().Contain("reformular");
    }

    private static List<ConversationMessage> Conversation(IReadOnlyList<ConversationMessage> history) =>
        history.Where(m => m.Role != MessageRole.System).ToList();
}
