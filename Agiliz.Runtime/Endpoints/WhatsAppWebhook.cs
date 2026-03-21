using Agiliz.Core.Twilio;
using Agiliz.Runtime.Services;
using Microsoft.AspNetCore.Mvc;

namespace Agiliz.Runtime.Endpoints;

public static class WhatsAppWebhook
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/webhook", HandleAsync)
           .DisableAntiforgery(); // Twilio envia form POST sem CSRF token
    }

    private static async Task<IResult> HandleAsync(
        [FromForm] string? From,
        [FromForm] string? To,
        [FromForm] string? Body,
        TenantRegistry registry,
        BotRunner runner,
        ITwilioSender sender,
        ILogger logger,
        CancellationToken ct)
    {
        // ─── Validação básica dos campos do webhook ───────────────────────────
        if (string.IsNullOrWhiteSpace(From) ||
            string.IsNullOrWhiteSpace(To) ||
            string.IsNullOrWhiteSpace(Body))
        {
            logger.LogWarning("Webhook recebido com campos ausentes. From={From} To={To}", From, To);
            return Results.BadRequest("Campos obrigatórios ausentes.");
        }

        // ─── Resolve o tenant pelo número de destino ──────────────────────────
        var tenant = registry.Resolve(To);
        if (tenant is null)
        {
            logger.LogWarning("Nenhum tenant encontrado para o número '{To}'.", To);
            return Results.Ok(); // Twilio precisa de 200; não há bot para este número
        }

        logger.LogInformation("Mensagem de {From} para [{Tenant}]: {Body}", From, tenant.Config.TenantId, Body);

        // ─── Processa e responde ──────────────────────────────────────────────
        var reply = await runner.ProcessAsync(tenant, From, Body, ct);

        await sender.SendAsync(
            toNumber: From,
            fromNumber: tenant.Config.TwilioNumber,
            body: reply,
            ct: ct);

        return Results.Ok();
    }
}
