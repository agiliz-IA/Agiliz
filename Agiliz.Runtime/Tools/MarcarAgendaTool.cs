using System.Text.Json;
using System.Text.Json.Nodes;
using Agiliz.Core.Models;
using Agiliz.Core.Tools;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Data.Entities;
using Agiliz.Runtime.Services;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Tools;

public sealed class MarcarAgendaTool(IServiceProvider services, GoogleCalendarService calendarService, TenantRegistry registry) : ITool
{
    public string Name => "marcar_agenda";
    public string Description => "Registra um agendamento real apos validar anti-fraude, LGPD, Google Calendar e conflitos locais.";

    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["nome"] = new JsonObject { ["type"] = "string" },
            ["dataHora"] = new JsonObject { ["type"] = "string", ["description"] = "Data e hora ISO 8601 com timezone" },
            ["formaPagamento"] = new JsonObject { ["type"] = "string" },
            ["consentimentoLgpd"] = new JsonObject { ["type"] = "boolean", ["description"] = "true somente quando o paciente aceitar salvar dados para o agendamento" }
        },
        ["required"] = new JsonArray { "nome", "dataHora", "formaPagamento" }
    };

    public async Task<ToolResult> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var ctx = RuntimeContext.Current.Value;
        if (ctx is null) return new ToolResult("Erro interno: contexto do usuario nao encontrado.");

        var tenant = registry.Get(ctx.TenantId);
        var googleCalendarId = (tenant is null ? null : Agiliz.Core.Integrations.BotIntegrations.CalendarId(tenant.Config));
        if (tenant is null || string.IsNullOrWhiteSpace(googleCalendarId))
            return new ToolResult("O calendario Google nao esta configurado para esta clinica. Avise o paciente.");

        using var doc = JsonDocument.Parse(arguments);
        var nome = doc.RootElement.GetProperty("nome").GetString() ?? "";
        var dataHoraStr = doc.RootElement.GetProperty("dataHora").GetString() ?? "";
        var pagamento = doc.RootElement.GetProperty("formaPagamento").GetString() ?? "";
        var consent = doc.RootElement.TryGetProperty("consentimentoLgpd", out var consentEl) && consentEl.ValueKind == JsonValueKind.True;

        if (!DateTimeOffset.TryParse(dataHoraStr, out var dataHora))
            return new ToolResult("Data/hora em formato invalido. Use ISO 8601 com timezone.");

        if (tenant.Config.LgpdConsentRequired && !consent)
            return new ToolResult("Antes de agendar, confirme se o paciente autoriza salvar nome, WhatsApp e dados do agendamento.");

        using var scope = services.CreateScope();
        var antiFraud = scope.ServiceProvider.GetRequiredService<AntiFraudService>();
        var db = scope.ServiceProvider.GetRequiredService<AgilizDbContext>();

        var eligibility = await antiFraud.CheckEligibilityAsync(ctx.UserPhone, ctx.TenantId);
        if (!eligibility.IsAllowed)
            return new ToolResult(eligibility.Reason);

        var duration = tenant.Config.Scheduling.AppointmentDurationMinutes;
        var localConflict = await db.Appointments.AnyAsync(a =>
            a.TenantId == ctx.TenantId &&
            a.Status != AppointmentStatus.Cancelled &&
            dataHora < a.ScheduledTime.AddMinutes(a.DurationMinutes) &&
            dataHora.AddMinutes(duration) > a.ScheduledTime,
            ct);

        if (localConflict)
            return new ToolResult("Esse horario ja foi preenchido. Verifique a agenda novamente e ofereca outro horario.");

        var available = await calendarService.IsSlotAvailableAsync(googleCalendarId, dataHora, tenant.Config.Scheduling, ct);
        if (!available)
            return new ToolResult("Esse horario nao esta disponivel na agenda. Verifique outros horarios antes de confirmar.");

        var googleEventId = await calendarService.CreateEventAsync(googleCalendarId, nome, dataHora, ctx.UserPhone, tenant.Config.Scheduling, ct);
        if (string.IsNullOrWhiteSpace(googleEventId))
            return new ToolResult("Erro ao salvar no Google Calendar. Tente novamente mais tarde.");

        var user = await db.Users.FindAsync([ctx.UserPhone, ctx.TenantId], ct);
        if (user is null)
        {
            user = new SchedulingUser
            {
                Phone = ctx.UserPhone,
                TenantId = ctx.TenantId,
                Name = nome,
                PaymentMethod = pagamento,
                LgpdConsentDate = consent ? DateTimeOffset.UtcNow : null
            };
            db.Users.Add(user);
        }
        else
        {
            user.Name = nome;
            user.PaymentMethod = pagamento;
            if (consent)
                user.LgpdConsentDate = DateTimeOffset.UtcNow;
        }

        db.Appointments.Add(new Appointment
        {
            Phone = ctx.UserPhone,
            TenantId = ctx.TenantId,
            ScheduledTime = dataHora,
            DurationMinutes = duration,
            Status = AppointmentStatus.Pending,
            GoogleEventId = googleEventId
        });

        await db.SaveChangesAsync(ct);
        return new ToolResult("Agendamento criado com sucesso. Informe ao paciente que ele recebera um lembrete antes da consulta.");
    }
}
