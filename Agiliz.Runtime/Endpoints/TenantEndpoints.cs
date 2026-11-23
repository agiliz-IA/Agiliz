using Agiliz.Core.Messaging;
using Agiliz.Core.Models;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Services;
using Microsoft.EntityFrameworkCore;

namespace Agiliz.Runtime.Endpoints;

/// <summary>
/// Dados de UM cliente, para o portal do cliente (chamado pelo painel, que confere quem está logado).
/// Mesmo token das rotas /api/admin: o navegador do cliente nunca fala direto com o Runtime.
/// </summary>
public static class TenantEndpoints
{
    public sealed record ReplyRequest(string? Body);
    public sealed record PortalCodeRequest(string Phone, string Code);

    public static void Map(WebApplication app)
    {
        var tenant = app.MapGroup("/api/admin/tenants/{tenantId}")
            .AddEndpointFilter<AdminTokenFilter>()
            .DisableAntiforgery();

        tenant.MapGet("/conversations", GetConversationsAsync);
        tenant.MapGet("/conversations/{phone}/messages", GetMessages);
        tenant.MapPost("/conversations/{phone}/pause", (string tenantId, string phone, TenantRegistry registry, SessionStore sessions) =>
        {
            if (registry.Get(tenantId) is null) return Results.NotFound();
            sessions.PauseSession(tenantId, phone);
            return Results.NoContent();
        });
        tenant.MapPost("/conversations/{phone}/resume", (string tenantId, string phone, TenantRegistry registry, SessionStore sessions) =>
        {
            if (registry.Get(tenantId) is null) return Results.NotFound();
            sessions.ResumeSession(tenantId, phone);
            return Results.NoContent();
        });
        tenant.MapPost("/conversations/{phone}/reply", ReplyAsync);
        tenant.MapGet("/appointments", GetAppointmentsAsync);
        tenant.MapPost("/portal-code", SendPortalCodeAsync);
        tenant.MapPost("/whatsapp/pairing", async (string tenantId, TenantRegistry registry, IMessageProvider messages, CancellationToken ct) =>
            registry.Get(tenantId) is { } t
                ? Results.Ok(await messages.RequestPairingCodeAsync(t.Config.WhatsAppNumber, t.Config.WhatsAppNumber, ct))
                : Results.NotFound());
        tenant.MapPost("/demo/reset", async (string tenantId, DemoResetService demo, CancellationToken ct) =>
        {
            try { return Results.Ok(await demo.ResetAsync(tenantId, ct)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
    }

    /// <summary>Conversas mais recentes (estado persistido) com o nome do contato quando o bot já o coletou.</summary>
    private static async Task<IResult> GetConversationsAsync(string tenantId, int? limit, TenantRegistry registry, AgilizDbContext db, CancellationToken ct)
    {
        if (registry.Get(tenantId) is null) return Results.NotFound();

        var rows = await db.ConversationStates.AsNoTracking()
            .Where(c => c.TenantId == tenantId)
            .OrderByDescending(c => c.LastActivityAt)
            .Take(Math.Clamp(limit ?? 50, 1, 200))
            .ToListAsync(ct);

        var phones = rows.Select(r => r.Phone).ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && phones.Contains(u.Phone))
            .ToDictionaryAsync(u => u.Phone, u => u.Name, ct);

        return Results.Ok(rows.Select(r => new
        {
            r.Phone,
            Name = names.GetValueOrDefault(r.Phone),
            r.IsPaused,
            r.PausedAt,
            r.LastActivityAt,
            r.LastMessagePreview
        }));
    }

    /// <summary>Últimas mensagens em memória (o Runtime guarda só a janela recente de cada conversa).</summary>
    private static IResult GetMessages(string tenantId, string phone, TenantRegistry registry, SessionStore sessions)
    {
        if (registry.Get(tenantId) is null) return Results.NotFound();
        var (messages, isPaused) = sessions.GetHistory(tenantId, phone);
        return Results.Ok(new
        {
            isPaused,
            messages = messages
                .Where(m => m.Role is MessageRole.User or MessageRole.Assistant)
                .Select(m => new { role = m.Role == MessageRole.User ? "cliente" : "bot", text = m.Content })
        });
    }

    /// <summary>Atendente responde pelo portal: a conversa fica com o humano até ele devolver ao bot.</summary>
    private static async Task<IResult> ReplyAsync(string tenantId, string phone, ReplyRequest request,
        TenantRegistry registry, SessionStore sessions, IMessageProvider messages, CancellationToken ct)
    {
        if (registry.Get(tenantId) is not { } t) return Results.NotFound();
        var body = request.Body?.Trim();
        if (string.IsNullOrEmpty(body)) return Results.BadRequest(new { error = "Mensagem vazia." });
        if (body.Length > 4000) return Results.BadRequest(new { error = "Mensagem longa demais." });

        await messages.SendAsync(phone, t.Config.WhatsAppNumber, body, ct);
        sessions.PauseSession(tenantId, phone);
        sessions.AddHumanReply(tenantId, phone, body);
        return Results.NoContent();
    }

    private static async Task<IResult> GetAppointmentsAsync(string tenantId, DateTimeOffset? from, DateTimeOffset? to,
        TenantRegistry registry, AgilizDbContext db, CancellationToken ct)
    {
        if (registry.Get(tenantId) is null) return Results.NotFound();
        var start = (from ?? DateTimeOffset.UtcNow.Date).ToUniversalTime();
        var end = (to ?? start.AddDays(1)).ToUniversalTime();

        var appointments = await db.Appointments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.ScheduledTime >= start && a.ScheduledTime < end)
            .OrderBy(a => a.ScheduledTime)
            .ToListAsync(ct);

        var phones = appointments.Select(a => a.Phone).Distinct().ToList();
        var names = await db.Users.AsNoTracking()
            .Where(u => u.TenantId == tenantId && phones.Contains(u.Phone))
            .ToDictionaryAsync(u => u.Phone, u => u.Name, ct);

        return Results.Ok(appointments.Select(a => new
        {
            a.Id,
            a.Phone,
            Name = names.GetValueOrDefault(a.Phone),
            a.ScheduledTime,
            a.DurationMinutes,
            Status = a.Status.ToString(),
            a.ConfirmedAt
        }));
    }

    /// <summary>
    /// Código de acesso ao portal. Sai do número do próprio bot; se ele ainda não estiver conectado,
    /// sai do número reserva (Portal:SenderNumber), se configurado.
    /// </summary>
    private static async Task<IResult> SendPortalCodeAsync(string tenantId, PortalCodeRequest request,
        TenantRegistry registry, IMessageProvider messages, IConfiguration config, ILoggerFactory loggers, CancellationToken ct)
    {
        if (registry.Get(tenantId) is not { } t) return Results.NotFound();
        if (request.Code is not { Length: 6 } || !request.Code.All(char.IsDigit)) return Results.BadRequest(new { error = "Código inválido." });

        var text = $"Seu código de acesso ao portal Agiliz é {request.Code}. Ele vale por 10 minutos. Se não foi você, ignore esta mensagem.";
        var logger = loggers.CreateLogger("PortalCode");
        try
        {
            await messages.SendAsync(request.Phone, t.Config.WhatsAppNumber, text, ct);
            return Results.NoContent();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            var fallback = config["Portal:SenderNumber"];
            if (string.IsNullOrWhiteSpace(fallback))
            {
                logger.LogWarning(ex, "[{Tenant}] Código do portal não enviado e sem Portal:SenderNumber", tenantId);
                return Results.Problem("Não foi possível enviar o código pelo WhatsApp.", statusCode: StatusCodes.Status502BadGateway);
            }

            logger.LogInformation("[{Tenant}] Número do bot indisponível; código do portal sai do número reserva", tenantId);
            await messages.SendAsync(request.Phone, fallback, text, ct);
            return Results.NoContent();
        }
    }
}
