using System.Text.Json;
using System.Text.Json.Nodes;
using Agiliz.Core.Tools;
using Agiliz.Core.Models;
using Agiliz.Runtime.Services;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;

namespace Agiliz.Runtime.Tools;

public sealed class MarcarAgendaTool(IServiceProvider services) : ITool
{
    public string Name => "marcar_agenda";
    public string Description => "Registra o agendamento no sistema. Retorna sucesso ou o motivo da recusa.";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["nome"] = new JsonObject { ["type"] = "string" },
            ["dataHora"] = new JsonObject { ["type"] = "string", ["description"] = "Data e Hora ISO 8601" },
            ["formaPagamento"] = new JsonObject { ["type"] = "string" }
        },
        ["required"] = new JsonArray { "nome", "dataHora", "formaPagamento" }
    };

    public async Task<ToolResult> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var ctx = RuntimeContext.Current.Value;
        if (ctx == null) return new ToolResult("Erro interno: Contexto do usuário não encontrado.");

        using var scope = services.CreateScope();
        var antiFraud = scope.ServiceProvider.GetRequiredService<AntiFraudService>();
        var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();

        var eligibility = await antiFraud.CheckEligibilityAsync(ctx.UserPhone, ctx.TenantId);
        if (!eligibility.IsAllowed)
            return new ToolResult(eligibility.Reason);

        using var doc = JsonDocument.Parse(arguments);
        var nome = doc.RootElement.GetProperty("nome").GetString() ?? "";
        var dataHoraStr = doc.RootElement.GetProperty("dataHora").GetString() ?? "";
        var pagamento = doc.RootElement.GetProperty("formaPagamento").GetString() ?? "";

        if (!DateTimeOffset.TryParse(dataHoraStr, out var dataHora))
            return new ToolResult("Data/Hora em formato inválido.");

        var user = await db.Users.FindAsync(new object[] { ctx.UserPhone, ctx.TenantId }, ct);
        if (user == null)
        {
            user = new SchedulingUser
            {
                Phone = ctx.UserPhone,
                TenantId = ctx.TenantId,
                Name = nome,
                PaymentMethod = pagamento,
                LgpdConsentDate = DateTimeOffset.UtcNow
            };
            db.Users.Add(user);
        }
        else
        {
            user.Name = nome;
            user.PaymentMethod = pagamento;
            db.Users.Update(user);
        }

        var appointment = new Appointment
        {
            Phone = ctx.UserPhone,
            TenantId = ctx.TenantId,
            ScheduledTime = dataHora,
            Status = AppointmentStatus.Pending
        };
        db.Appointments.Add(appointment);

        await db.SaveChangesAsync(ct);

        return new ToolResult("Agendamento criado com sucesso.");
    }
}
