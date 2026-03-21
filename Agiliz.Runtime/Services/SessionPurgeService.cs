namespace Agiliz.Runtime.Services;

/// <summary>
/// Roda em background e expurga sessões inativas do SessionStore.
/// Evita que a memória cresça indefinidamente em instâncias de longa duração.
/// </summary>
public sealed class SessionPurgeService(SessionStore store, ILogger<SessionPurgeService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(Interval, ct);
            store.PurgeExpired();
            logger.LogDebug("Sessões expiradas removidas.");
        }
    }
}
