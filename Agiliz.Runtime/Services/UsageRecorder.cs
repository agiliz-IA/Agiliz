using System.Threading.Channels;
using Agiliz.Core.Plans;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Services;

public interface IUsageRecorder
{
    /// <summary>Conta uma mensagem recebida do contato. Só enfileira; não espera o banco.</summary>
    void RecordInbound(string tenantId, string phone);
}

/// <summary>
/// Grava contatos por mês (ContactActivity) em segundo plano, como o BillingWriter.
/// Mensagens do mesmo contato no mesmo lote viram uma atualização só.
/// </summary>
public sealed class UsageRecorder(IServiceScopeFactory scopes, ILogger<UsageRecorder> logger) : BackgroundService, IUsageRecorder
{
    private const int MaxBatch = 500;
    private const int MaxQueued = 20_000;

    private readonly Channel<(string TenantId, string Phone, DateTimeOffset At)> _queue =
        Channel.CreateBounded<(string, string, DateTimeOffset)>(new BoundedChannelOptions(MaxQueued) { SingleReader = true });

    public void RecordInbound(string tenantId, string phone)
    {
        if (!_queue.Writer.TryWrite((tenantId, phone, DateTimeOffset.UtcNow)))
            logger.LogWarning("Fila de uso cheia; mensagem de {Tenant} não contada na franquia.", tenantId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<(string TenantId, string Phone, DateTimeOffset At)>(MaxBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var item)) batch.Add(item);
                await SaveAsync(batch, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (_queue.Reader.TryRead(out var item)) batch.Add(item);
        if (batch.Count > 0) await SaveAsync(batch, shutdown.Token);
    }

    private async Task SaveAsync(List<(string TenantId, string Phone, DateTimeOffset At)> batch, CancellationToken ct)
    {
        try
        {
            var grouped = batch
                .GroupBy(b => (b.TenantId, Month: UsageMonth.For(b.At), b.Phone))
                .Select(g => (g.Key.TenantId, g.Key.Month, g.Key.Phone, First: g.Min(x => x.At), Last: g.Max(x => x.At), Count: g.Count()))
                .ToList();

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();

            var tenants = grouped.Select(g => g.TenantId).Distinct().ToList();
            var months = grouped.Select(g => g.Month).Distinct().ToList();
            var phones = grouped.Select(g => g.Phone).Distinct().ToList();
            var existing = (await db.ContactActivities
                    .Where(c => tenants.Contains(c.TenantId) && months.Contains(c.Month) && phones.Contains(c.Phone))
                    .ToListAsync(ct))
                .ToDictionary(c => (c.TenantId, c.Month, c.Phone));

            foreach (var g in grouped)
            {
                if (existing.TryGetValue((g.TenantId, g.Month, g.Phone), out var row))
                {
                    row.LastSeenAt = g.Last;
                    row.MessageCount += g.Count;
                }
                else
                {
                    db.ContactActivities.Add(new ContactActivity
                    {
                        TenantId = g.TenantId, Month = g.Month, Phone = g.Phone,
                        FirstSeenAt = g.First, LastSeenAt = g.Last, MessageCount = g.Count
                    });
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Contagem da franquia é informativa: falha não pode afetar o atendimento.
            logger.LogError(ex, "Falha ao gravar uso de {Count} mensagens.", batch.Count);
        }
    }
}
