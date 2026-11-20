using System.Collections.Concurrent;
using Agiliz.Core.Models;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Services;

/// <summary>
/// Histórico de conversa em memória, chaveado por número do usuário.
/// Stateless entre sessões: entradas expiram após <see cref="SessionTtl"/> de inatividade.
/// </summary>
public sealed class SessionStore
{
    public record SessionInfo(IReadOnlyList<ConversationMessage> History, int TurnCount, decimal AccumulatedCostUsd, bool IsPaused);

    private const int MaxMessagesPerSession = 10; // 5 trocas (user + assistant)
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly IServiceProvider? _services;

    public SessionStore(IServiceProvider? services = null)
    {
        _services = services;
    }

    public SessionInfo AddAndGet(string userPhone, ConversationMessage message) =>
        AddAndGet("default", userPhone, message);

    public void AddAssistantReply(string userPhone, string reply) =>
        AddAssistantReply("default", userPhone, reply);

    public void AddCost(string userPhone, decimal costUsd) =>
        AddCost("default", userPhone, costUsd);

    public void PauseSession(string userPhone) =>
        PauseSession("default", userPhone);

    public void ResumeSession(string userPhone) =>
        ResumeSession("default", userPhone);

    /// <summary>
    /// Retorna o histórico atual do usuário e adiciona a nova mensagem.
    /// Descarta as mais antigas se ultrapassar o limite.
    /// </summary>
    public SessionInfo AddAndGet(string tenantId, string userPhone, ConversationMessage message)
    {
        var key = BuildKey(tenantId, userPhone);
        var persistedPause = IsPausedPersisted(tenantId, userPhone);
        var session = _sessions.AddOrUpdate(
            key,
            _ => new Session([message]) { TurnCount = 1, IsPaused = persistedPause },
            (_, existing) =>
            {
                existing.Messages.Add(message);
                
                if (message.Role == MessageRole.User)
                    existing.TurnCount++;

                // Janela deslizante: remove o par mais antigo se exceder o limite
                while (existing.Messages.Count > MaxMessagesPerSession)
                    existing.Messages.RemoveRange(0, 2);

                existing.LastActivity = DateTimeOffset.UtcNow;
                existing.IsPaused = existing.IsPaused || persistedPause;
                return existing;
            });

        TouchConversation(tenantId, userPhone, message.Content);
        return new SessionInfo(session.Messages.AsReadOnly(), session.TurnCount, session.AccumulatedCostUsd, session.IsPaused);
    }

    /// <summary>
    /// Adiciona a resposta do assistente ao histórico existente.
    /// </summary>
    public void AddAssistantReply(string tenantId, string userPhone, string reply)
    {
        if (_sessions.TryGetValue(BuildKey(tenantId, userPhone), out var session))
        {
            session.Messages.Add(ConversationMessage.Assistant(reply));
            session.LastActivity = DateTimeOffset.UtcNow;
        }
    }

    public void AddCost(string tenantId, string userPhone, decimal costUsd)
    {
        if (_sessions.TryGetValue(BuildKey(tenantId, userPhone), out var session))
        {
            session.AccumulatedCostUsd += costUsd;
        }
    }

    public void PauseSession(string tenantId, string userPhone)
    {
        if (_sessions.TryGetValue(BuildKey(tenantId, userPhone), out var session))
        {
            session.IsPaused = true;
            session.LastActivity = DateTimeOffset.UtcNow;
        }

        PersistPause(tenantId, userPhone, isPaused: true);
    }

    public void ResumeSession(string tenantId, string userPhone)
    {
        if (_sessions.TryGetValue(BuildKey(tenantId, userPhone), out var session))
        {
            session.IsPaused = false;
            session.LastActivity = DateTimeOffset.UtcNow;
        }

        PersistPause(tenantId, userPhone, isPaused: false);
    }

    /// <summary>
    /// Remove sessões inativas. Deve ser chamado periodicamente (ex: hosted service ou timer).
    /// </summary>
    public void PurgeExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - SessionTtl;
        foreach (var key in _sessions.Keys)
        {
            // Sessões pausadas não expiram tão rápido, pois dependem de intervenção humana (vamos dar 24h)
            var sessionCutoff = _sessions.TryGetValue(key, out var s) && s.IsPaused 
                ? DateTimeOffset.UtcNow - TimeSpan.FromHours(24) 
                : cutoff;

            if (s != null && s.LastActivity < sessionCutoff)
                _sessions.TryRemove(key, out _);
        }
    }

    private sealed class Session(List<ConversationMessage> messages)
    {
        public List<ConversationMessage> Messages { get; } = messages;
        public int TurnCount { get; set; }
        public decimal AccumulatedCostUsd { get; set; }
        public bool IsPaused { get; set; }
        public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.UtcNow;
    }

    private static string BuildKey(string tenantId, string userPhone) => $"{tenantId.Trim().ToLowerInvariant()}::{userPhone}";

    private bool IsPausedPersisted(string tenantId, string userPhone)
    {
        if (_services is null) return false;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetService<AgilizDbContext>();
            if (db is null) return false;

            return db.ConversationStates
                .AsNoTracking()
                .Any(s => s.TenantId == tenantId && s.Phone == userPhone && s.IsPaused);
        }
        catch
        {
            return false;
        }
    }

    private void TouchConversation(string tenantId, string userPhone, string message)
    {
        if (_services is null) return;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetService<AgilizDbContext>();
            if (db is null) return;

            var state = db.ConversationStates.Find(userPhone, tenantId);
            if (state is null)
            {
                state = new ConversationState { Phone = userPhone, TenantId = tenantId };
                db.ConversationStates.Add(state);
            }

            state.LastActivityAt = DateTimeOffset.UtcNow;
            state.LastMessagePreview = SanitizePreview(message);
            db.SaveChanges();
        }
        catch { }
    }

    private void PersistPause(string tenantId, string userPhone, bool isPaused)
    {
        if (_services is null) return;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetService<AgilizDbContext>();
            if (db is null) return;

            var state = db.ConversationStates.Find(userPhone, tenantId);
            if (state is null)
            {
                state = new ConversationState { Phone = userPhone, TenantId = tenantId };
                db.ConversationStates.Add(state);
            }

            state.IsPaused = isPaused;
            state.LastActivityAt = DateTimeOffset.UtcNow;
            state.PausedAt = isPaused ? DateTimeOffset.UtcNow : null;
            db.SaveChanges();
        }
        catch { }
    }

    private static string SanitizePreview(string value)
    {
        var preview = value.ReplaceLineEndings(" ").Trim();
        return preview.Length <= 160 ? preview : preview[..160];
    }
}
