using Agiliz.Core.Integrations;
using Agiliz.Runtime.Data;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Services;

public sealed record DemoResetResult(int Appointments, int Contacts, int Conversations, int CalendarEvents);

/// <summary>
/// Deixa os bots de demonstração (BotConfig.IsDemo) como novos: toda madrugada (3h, Brasília) e quando
/// o vendedor pede no portal. Apaga agenda, conversas, contatos e nomes de quem testou (são dados de leads).
/// Os custos ficam: não têm dado pessoal e mostram quanto a demo gasta.
/// </summary>
public sealed class DemoResetService(IServiceScopeFactory scopes, TenantRegistry registry, SessionStore sessions, ILogger<DemoResetService> logger)
    : BackgroundService
{
    private static readonly TimeOnly ResetAt = new(3, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(DelayUntilNextReset(DateTimeOffset.UtcNow), stoppingToken);
            }
            catch (OperationCanceledException) { return; }

            foreach (var tenant in registry.All().Where(t => t.Config.IsDemo))
            {
                try
                {
                    var result = await ResetAsync(tenant.Config.TenantId, stoppingToken);
                    logger.LogInformation("Demo {Tenant} limpa: {Result}", tenant.Config.TenantId, result);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Falha ao limpar a demo {Tenant}", tenant.Config.TenantId);
                }
            }
        }
    }

    public static TimeSpan DelayUntilNextReset(DateTimeOffset utcNow)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT"); }

        var local = TimeZoneInfo.ConvertTime(utcNow, zone);
        var next = new DateTimeOffset(local.Date.Add(ResetAt.ToTimeSpan()), local.Offset);
        if (next <= local) next = next.AddDays(1);
        return next - local;
    }

    /// <summary>Limpa um bot de demonstração. Lança InvalidOperationException se o bot não for demo.</summary>
    public async Task<DemoResetResult> ResetAsync(string tenantId, CancellationToken ct = default)
    {
        var tenant = registry.Get(tenantId);
        if (tenant?.Config.IsDemo != true)
            throw new InvalidOperationException($"'{tenantId}' não é um bot de demonstração.");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();

        var appointments = await db.Appointments.Where(a => a.TenantId == tenantId).ToListAsync(ct);

        // Eventos criados pela demo saem da agenda de exemplo também.
        var removedEvents = 0;
        var calendarId = BotIntegrations.CalendarId(tenant.Config);
        if (calendarId is not null && appointments.Any(a => a.GoogleEventId is not null))
        {
            var calendar = scope.ServiceProvider.GetService<GoogleCalendarService>();
            if (calendar?.IsConfigured == true)
                foreach (var a in appointments.Where(a => a.GoogleEventId is not null))
                    if (await calendar.DeleteEventAsync(calendarId, a.GoogleEventId!, ct))
                        removedEvents++;
        }

        db.Appointments.RemoveRange(appointments);
        db.Users.RemoveRange(await db.Users.Where(u => u.TenantId == tenantId).ToListAsync(ct));
        var conversations = await db.ConversationStates.Where(c => c.TenantId == tenantId).ToListAsync(ct);
        db.ConversationStates.RemoveRange(conversations);
        var contacts = await db.ContactActivities.Where(c => c.TenantId == tenantId).ToListAsync(ct);
        db.ContactActivities.RemoveRange(contacts);
        await db.SaveChangesAsync(ct);

        var inMemory = sessions.ClearTenant(tenantId);
        return new DemoResetResult(appointments.Count, contacts.Count, Math.Max(conversations.Count, inMemory), removedEvents);
    }
}
