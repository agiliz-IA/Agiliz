using System.Threading.Channels;
using Agiliz.Core.Billing;
using Agiliz.Core.Models;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;

namespace Agiliz.Runtime.Services;

/// <summary>
/// Grava os custos no Postgres em segundo plano. <see cref="Record"/> só põe o lançamento numa fila,
/// então banco lento ou fora do ar não atrasa a resposta ao cliente no WhatsApp.
/// Substitui o configs/billing.json, que era escrito por dois containers ao mesmo tempo e perdia lançamentos.
/// </summary>
public sealed class BillingWriter(IServiceScopeFactory scopes, ILogger<BillingWriter> logger) : BackgroundService, ICostRecorder
{
    private const int MaxBatch = 200;
    private const int MaxAttempts = 3;
    private const int MaxQueued = 10_000;

    private readonly Channel<CostEntry> _queue = Channel.CreateBounded<CostEntry>(
        new BoundedChannelOptions(MaxQueued) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public void Record(CostEntry entry)
    {
        if (!_queue.Writer.TryWrite(entry))
            logger.LogError("Fila de billing cheia; custo descartado: {Tenant} '{Description}' US$ {Amount}",
                entry.TenantId, entry.Description, entry.AmountUsd);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<CostEntry>(MaxBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken))
            {
                Drain(batch);
                await SaveAsync(batch, MaxAttempts, stoppingToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligando: grava abaixo o que ainda estiver na fila.
        }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Drain(batch);
        while (batch.Count > 0 && !shutdown.IsCancellationRequested)
        {
            await SaveAsync(batch, maxAttempts: 1, shutdown.Token);
            batch.Clear();
            Drain(batch);
        }
    }

    private void Drain(List<CostEntry> batch)
    {
        while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var entry))
            batch.Add(entry);
    }

    private async Task SaveAsync(List<CostEntry> batch, int maxAttempts, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();
                db.CostRecords.AddRange(batch.Select(CostRecord.From));
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= maxAttempts)
                {
                    logger.LogError(ex, "Billing: {Count} lançamentos (US$ {Total}) não gravados após {Attempts} tentativa(s).",
                        batch.Count, batch.Sum(e => e.AmountUsd), attempt);
                    return;
                }

                logger.LogWarning(ex, "Billing: falha ao gravar {Count} lançamentos (tentativa {Attempt}); tentando de novo.",
                    batch.Count, attempt);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }
}
