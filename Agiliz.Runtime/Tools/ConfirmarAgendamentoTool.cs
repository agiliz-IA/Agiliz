using System.Text.Json.Nodes;
using Agiliz.Core.Models;
using Agiliz.Core.Tools;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Agiliz.Runtime.Services;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Tools;

public sealed class ConfirmarAgendamentoTool(IServiceProvider services) : ITool
{
    public string Name => "confirmar_agendamento";
    public string Description => "Confirma o proximo agendamento pendente do paciente.";
    public JsonObject ParametersSchema => new() { ["type"] = "object", ["properties"] = new JsonObject() };

    public async Task<ToolResult> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var ctx = RuntimeContext.Current.Value;
        if (ctx is null) return new ToolResult("Erro interno: contexto do usuario nao encontrado.");

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();
        var appointment = await db.Appointments
            .Where(a => a.TenantId == ctx.TenantId && a.Phone == ctx.UserPhone && a.Status == AppointmentStatus.Pending)
            .OrderBy(a => a.ScheduledTime)
            .FirstOrDefaultAsync(ct);

        if (appointment is null)
            return new ToolResult("Nao encontrei agendamento pendente para confirmar.");

        appointment.Status = AppointmentStatus.Confirmed;
        appointment.ConfirmedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new ToolResult("Agendamento confirmado com sucesso.");
    }
}
