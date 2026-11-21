using System.Text.Json;
using System.Text.Json.Nodes;
using Agiliz.Core.Models;
using Agiliz.Core.Tools;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Agiliz.Runtime.Services;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Tools;

public sealed class ReagendarAgendamentoTool(IServiceProvider services, GoogleCalendarService calendarService, TenantRegistry registry) : ITool
{
    public string Name => "reagendar_agendamento";
    public string Description => "Move o proximo agendamento ativo do paciente para uma nova data/hora validada.";
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["novaDataHora"] = new JsonObject { ["type"] = "string", ["description"] = "Nova data e hora ISO 8601 com timezone" }
        },
        ["required"] = new JsonArray { "novaDataHora" }
    };

    public async Task<ToolResult> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var ctx = RuntimeContext.Current.Value;
        if (ctx is null) return new ToolResult("Erro interno: contexto do usuario nao encontrado.");

        var tenant = registry.Get(ctx.TenantId);
        var calendarId = (tenant is null ? null : Agiliz.Core.Integrations.BotIntegrations.CalendarId(tenant.Config));
        if (tenant is null || string.IsNullOrWhiteSpace(calendarId))
            return new ToolResult("O calendario Google nao esta configurado para esta clinica.");

        using var doc = JsonDocument.Parse(arguments);
        var novaDataHoraStr = doc.RootElement.GetProperty("novaDataHora").GetString() ?? "";
        if (!DateTimeOffset.TryParse(novaDataHoraStr, out var novaDataHora))
            return new ToolResult("Nova data/hora em formato invalido. Use ISO 8601 com timezone.");

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();
        var appointment = await db.Appointments
            .Where(a => a.TenantId == ctx.TenantId && a.Phone == ctx.UserPhone && a.Status != AppointmentStatus.Cancelled)
            .OrderBy(a => a.ScheduledTime)
            .FirstOrDefaultAsync(ct);

        if (appointment is null)
            return new ToolResult("Nao encontrei agendamento ativo para reagendar.");

        var duration = tenant.Config.Scheduling.AppointmentDurationMinutes;
        var localConflict = await db.Appointments.AnyAsync(a =>
            a.Id != appointment.Id &&
            a.TenantId == ctx.TenantId &&
            a.Status != AppointmentStatus.Cancelled &&
            novaDataHora < a.ScheduledTime.AddMinutes(a.DurationMinutes) &&
            novaDataHora.AddMinutes(duration) > a.ScheduledTime,
            ct);

        if (localConflict)
            return new ToolResult("Esse novo horario ja esta ocupado. Verifique a agenda e ofereca outro horario.");

        var available = await calendarService.IsSlotAvailableAsync(calendarId, novaDataHora, tenant.Config.Scheduling, ct);
        if (!available)
            return new ToolResult("Esse novo horario nao esta disponivel na agenda.");

        if (!string.IsNullOrWhiteSpace(appointment.GoogleEventId))
        {
            var moved = await calendarService.MoveEventAsync(calendarId, appointment.GoogleEventId, novaDataHora, tenant.Config.Scheduling, ct);
            if (!moved)
                return new ToolResult("Nao consegui reagendar no Google Calendar. Tente novamente mais tarde.");
        }

        appointment.ScheduledTime = novaDataHora;
        appointment.DurationMinutes = duration;
        appointment.Status = AppointmentStatus.Pending;
        appointment.ConfirmedAt = null;
        appointment.ReminderSentAt = null;
        await db.SaveChangesAsync(ct);
        return new ToolResult("Agendamento reagendado com sucesso.");
    }
}
