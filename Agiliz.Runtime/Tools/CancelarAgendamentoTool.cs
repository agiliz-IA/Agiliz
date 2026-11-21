using System.Text.Json.Nodes;
using Agiliz.Core.Models;
using Agiliz.Core.Tools;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Agiliz.Runtime.Services;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Tools;

public sealed class CancelarAgendamentoTool(IServiceProvider services, GoogleCalendarService calendarService, TenantRegistry registry) : ITool
{
    public string Name => "cancelar_agendamento";
    public string Description => "Cancela o proximo agendamento ativo do paciente no banco local e no Google Calendar.";
    public JsonObject ParametersSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public async Task<ToolResult> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var ctx = RuntimeContext.Current.Value;
        if (ctx is null) return new ToolResult("Erro interno: contexto do usuario nao encontrado.");

        var tenant = registry.Get(ctx.TenantId);
        var calendarId = (tenant is null ? null : Agiliz.Core.Integrations.BotIntegrations.CalendarId(tenant.Config));
        if (tenant is null || string.IsNullOrWhiteSpace(calendarId))
            return new ToolResult("O calendario Google nao esta configurado para esta clinica.");

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();
        var appointment = await db.Appointments
            .Where(a => a.TenantId == ctx.TenantId && a.Phone == ctx.UserPhone && a.Status != AppointmentStatus.Cancelled)
            .OrderBy(a => a.ScheduledTime)
            .FirstOrDefaultAsync(ct);

        if (appointment is null)
            return new ToolResult("Nao encontrei agendamento ativo para cancelar.");

        if (!string.IsNullOrWhiteSpace(appointment.GoogleEventId))
        {
            var deleted = await calendarService.DeleteEventAsync(calendarId, appointment.GoogleEventId, ct);
            if (!deleted)
                return new ToolResult("Nao consegui cancelar no Google Calendar. Tente novamente mais tarde.");
        }

        appointment.Status = AppointmentStatus.Cancelled;
        appointment.CancelledAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new ToolResult("Agendamento cancelado com sucesso.");
    }
}
