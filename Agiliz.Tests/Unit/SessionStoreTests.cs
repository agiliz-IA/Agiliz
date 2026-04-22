using Agiliz.Core.Models;
using Agiliz.Runtime.Services;
using FluentAssertions;
using Xunit;

namespace Agiliz.Tests.Unit;

public sealed class SessionStoreTests
{
    private readonly SessionStore _store = new();
    private const string Phone = "+5521999999999";

    // ─── AddAndGet ────────────────────────────────────────────────────────────

    [Fact]
    public void AddAndGet_FirstMessage_ReturnsSingleEntryHistory()
    {
        var history = _store.AddAndGet(Phone, ConversationMessage.User("oi")).History;

        history.Should().ContainSingle(m => m.Role == MessageRole.User && m.Content == "oi");
    }

    [Fact]
    public void AddAndGet_MultipleCalls_AccumulatesMessages()
    {
        _store.AddAndGet(Phone, ConversationMessage.User("msg1"));
        _store.AddAssistantReply(Phone, "resposta1");
        var history = _store.AddAndGet(Phone, ConversationMessage.User("msg2")).History;

        history.Should().HaveCount(3);
        history[0].Content.Should().Be("msg1");
        history[1].Content.Should().Be("resposta1");
        history[2].Content.Should().Be("msg2");
    }

    [Fact]
    public void AddAndGet_DifferentPhones_AreIsolated()
    {
        _store.AddAndGet("+5521111", ConversationMessage.User("alice"));
        var bobHistory = _store.AddAndGet("+5522222", ConversationMessage.User("bob")).History;

        bobHistory.Should().ContainSingle(m => m.Content == "bob");
    }

    // ─── Janela deslizante ────────────────────────────────────────────────────

    [Fact]
    public void AddAndGet_WhenExceedsMaxMessages_RemovesOldestPair()
    {
        // Preenche com 5 trocas (10 mensagens = limite)
        for (var i = 1; i <= 5; i++)
        {
            _store.AddAndGet(Phone, ConversationMessage.User($"user{i}"));
            _store.AddAssistantReply(Phone, $"bot{i}");
        }

        // A 11ª mensagem deve acionar o descarte do par mais antigo
        var history = _store.AddAndGet(Phone, ConversationMessage.User("user6")).History;

        history.Should().HaveCount(9); // ainda 10 (removeu 1 par, adicionou 1)
        history[0].Content.Should().Be("user2"); // user1/bot1 foram descartados
    }

    [Fact]
    public void AddAndGet_SlidingWindow_PreservesLatestMessages()
    {
        for (var i = 1; i <= 6; i++)
        {
            _store.AddAndGet(Phone, ConversationMessage.User($"user{i}"));
            _store.AddAssistantReply(Phone, $"bot{i}");
        }

        var history = _store.AddAndGet(Phone, ConversationMessage.User("user7")).History;

        // As mensagens mais recentes devem estar presentes
        history.Select(m => m.Content).Should().Contain("user6", "bot6", "user7");
        history.Select(m => m.Content).Should().NotContain("user1", "bot1");
    }

    // ─── AddAssistantReply ────────────────────────────────────────────────────

    [Fact]
    public void AddAssistantReply_WhenSessionExists_AppendsToHistory()
    {
        _store.AddAndGet(Phone, ConversationMessage.User("pergunta"));
        _store.AddAssistantReply(Phone, "resposta");

        var history = _store.AddAndGet(Phone, ConversationMessage.User("outra")).History;
        history.Should().HaveCount(3);
        history[1].Role.Should().Be(MessageRole.Assistant);
        history[1].Content.Should().Be("resposta");
    }

    [Fact]
    public void AddAssistantReply_WhenSessionDoesNotExist_DoesNotThrow()
    {
        var act = () => _store.AddAssistantReply("+5599999", "resposta");
        act.Should().NotThrow();
    }

    // ─── PurgeExpired ─────────────────────────────────────────────────────────

    [Fact]
    public void PurgeExpired_SessionWithRecentActivity_IsPreserved()
    {
        _store.AddAndGet(Phone, ConversationMessage.User("recente"));
        _store.PurgeExpired();

        // Deve continuar existindo
        var history = _store.AddAndGet(Phone, ConversationMessage.User("ping")).History;
        history.Should().HaveCount(2); // recente + ping
    }

    [Fact]
    public void PurgeExpired_NewSession_IsNotPurged()
    {
        _store.AddAndGet(Phone, ConversationMessage.User("msg"));

        // Chamar PurgeExpired imediatamente não deve remover sessão recente
        _store.PurgeExpired();

        var history = _store.AddAndGet(Phone, ConversationMessage.User("ping")).History;
        history.Should().HaveCount(2);
    }
}
