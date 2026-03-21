using System.Collections.Concurrent;
using Agiliz.Core.Models;

namespace Agiliz.Runtime.Services;

/// <summary>
/// Histórico de conversa em memória, chaveado por número do usuário.
/// Stateless entre sessões: entradas expiram após <see cref="SessionTtl"/> de inatividade.
/// </summary>
public sealed class SessionStore
{
    private const int MaxMessagesPerSession = 10; // 5 trocas (user + assistant)
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    /// <summary>
    /// Retorna o histórico atual do usuário e adiciona a nova mensagem.
    /// Descarta as mais antigas se ultrapassar o limite.
    /// </summary>
    public IReadOnlyList<ConversationMessage> AddAndGet(string userPhone, ConversationMessage message)
    {
        var session = _sessions.AddOrUpdate(
            userPhone,
            _ => new Session([message]),
            (_, existing) =>
            {
                existing.Messages.Add(message);

                // Janela deslizante: remove o par mais antigo se exceder o limite
                while (existing.Messages.Count > MaxMessagesPerSession)
                    existing.Messages.RemoveRange(0, 2);

                existing.LastActivity = DateTimeOffset.UtcNow;
                return existing;
            });

        return session.Messages.AsReadOnly();
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
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    }
}
