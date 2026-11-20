using Agiliz.Core.Messaging;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Agiliz.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Services;

public class ReminderBackgroundService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ReminderBackgroundService> _logger;

    public ReminderBackgroundService(IServiceProvider services, ILogger<ReminderBackgroundService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ReminderBackgroundService está iniciando.");
        
        // Loop que roda a cada 10 minutos
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));

        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await ProcessRemindersAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao processar lembretes ativos.");
            }
        }
    }

    private async Task ProcessRemindersAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();
        var registry = scope.ServiceProvider.GetRequiredService<TenantRegistry>();
        var messageSender = scope.ServiceProvider.GetRequiredService<IMessageProvider>();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionStore>();

        var now = DateTimeOffset.UtcNow;
        var lowerBound = now.AddHours(23);
        var upperBound = now.AddHours(25);

        // Busca agendamentos Pending que não receberam lembrete e estão entre 23-25h a partir de agora
        var upcomingAppointments = await db.Appointments
            .Where(a => a.Status == AppointmentStatus.Pending 
                     && a.ReminderSentAt == null 
                     && a.ScheduledTime >= lowerBound 
                     && a.ScheduledTime <= upperBound)
            .ToListAsync(ct);

        if (!upcomingAppointments.Any()) return;

        _logger.LogInformation("Encontrados {Count} agendamentos para envio de lembrete.", upcomingAppointments.Count);

        var groupedByTenant = upcomingAppointments.GroupBy(a => a.TenantId);

        foreach (var group in groupedByTenant)
        {
            var tenantId = group.Key;
            var tenant = registry.Get(tenantId);
            
            if (tenant == null)
            {
                _logger.LogWarning("Tenant '{TenantId}' não encontrado. Ignorando lembretes.", tenantId);
                continue;
            }

            var tenantPhones = group.Select(a => a.Phone).ToList();
            var users = await db.Users
                .Where(u => u.TenantId == tenantId && tenantPhones.Contains(u.Phone))
                .ToListAsync(ct);

            foreach (var appointment in group)
            {
                var user = users.FirstOrDefault(u => u.Phone == appointment.Phone);
                var userName = user?.Name ?? "paciente";

                // Formatar hora do agendamento para fuso do Brasil (exemplo simples, será aprimorado no Pilar 5)
                var localTime = appointment.ScheduledTime.ToOffset(TimeSpan.FromHours(-3));
                var timeStr = localTime.ToString("HH:mm");

                var messageText = $"Olá {userName}, tudo bem? Estou passando para confirmar sua consulta amanhã às {timeStr}.";
                var buttons = new[] { "Confirmar", "Remarcar" };

                try
                {
                    // Disparo ativo com Botões Nativos
                    await messageSender.SendButtonsAsync(
                        toNumber: appointment.Phone,
                        fromNumber: tenant.Config.WhatsAppNumber,
                        body: messageText,
                        buttons: buttons,
                        ct: ct
                    );

                    // Alimentar a SessionStore para dar contexto ao LLM
                    var textForLlm = $"{messageText}\n[Opções fornecidas ao usuário: Confirmar, Remarcar]";
                    sessions.AddAssistantReply(tenantId, appointment.Phone, textForLlm);

                    appointment.ReminderSentAt = now;
                    _logger.LogInformation("Lembrete enviado para {Phone} (Tenant: {TenantId})", appointment.Phone, tenantId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha ao enviar lembrete para {Phone}", appointment.Phone);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
