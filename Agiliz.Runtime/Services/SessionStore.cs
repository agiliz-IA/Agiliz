using System.Collections.Concurrent;
using Agiliz.Core.Models;

namespace Agiliz.Runtime.Services;

/// <summary>
/// Histórico de conversa em memória, chaveado por número do usuário.
/// Stateless entre sessões: entradas expiram após <see cref="SessionTtl"/> de inatividade.
/// </summary>
public sealed class SessionStore
{
    public record SessionInfo(IReadOnlyList<ConversationMessage> History, int TurnCount, decimal AccumulatedCostUsd);

    private const int MaxMessagesPerSession = 10; // 5 trocas (user + assistant)
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <summary>
    /// Retorna o histórico atual do usuário e adiciona a nova mensagem.
    /// Descarta as mais antigas se ultrapassar o limite.
    /// </summary>
    public SessionInfo AddAndGet(string userPhone, ConversationMessage message)
    {
        var session = _sessions.AddOrUpdate(
            userPhone,
            _ => new Session([message]) { TurnCount = 1 },
            (_, existing) =>
            {
                existing.Messages.Add(message);
                
                if (message.Role == MessageRole.User)
                    existing.TurnCount++;

                // Janela deslizante: remove o par mais antigo se exceder o limite
                while (existing.Messages.Count > MaxMessagesPerSession)
                    existing.Messages.RemoveRange(0, 2);

                existing.LastActivity = DateTimeOffset.UtcNow;
                return existing;
            });

        return new SessionInfo(session.Messages.AsReadOnly(), session.TurnCount, session.AccumulatedCostUsd);
    }

    /// <summary>
    /// Adiciona a resposta do assistente ao histórico existente.
    /// </summary>
    public void AddAssistantReply(string userPhone, string reply)
    {
        if (_sessions.TryGetValue(userPhone, out var session))
        {
            session.Messages.Add(ConversationMessage.Assistant(reply));
            session.LastActivity = DateTimeOffset.UtcNow;
        }
    }

    public void AddCost(string userPhone, decimal costUsd)
    {
        if (_sessions.TryGetValue(userPhone, out var session))
        {
            session.AccumulatedCostUsd += costUsd;
        }
    }

    /// <summary>
    /// Remove sessões inativas. Deve ser chamado periodicamente (ex: hosted service ou timer).
    /// </summary>
    public void PurgeExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - SessionTtl;
        foreach (var key in _sessions.Keys)
        {
            if (_sessions.TryGetValue(key, out var s) && s.LastActivity < cutoff)
                _sessions.TryRemove(key, out _);
        }
    }

    private sealed class Session(List<ConversationMessage> messages)
    {
        public List<ConversationMessage> Messages { get; } = messages;
        public int TurnCount { get; set; }
        public decimal AccumulatedCostUsd { get; set; }
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    }
}
