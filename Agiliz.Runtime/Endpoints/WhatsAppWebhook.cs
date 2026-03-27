using Agiliz.Core.Messaging;
using Agiliz.Runtime.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace Agiliz.Runtime.Endpoints;

public static class WhatsAppWebhook
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/webhook", HandleAsync)
           .DisableAntiforgery(); // Evolution envia JSON POST, CSRF desabilitado
    }

    private static async Task<IResult> HandleAsync(
        EvolutionWebhookPayload? payload,
        TenantRegistry registry,
        BotRunner runner,
        IMessageProvider messageSender,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("WhatsAppWebhook");

        // ─── Validação básica do payload ──────────────────────────────────────
        if (payload?.Data?.Message == null)
        {
            logger.LogWarning("Webhook Evolution recebido com payload inválido");
            return Results.BadRequest("Payload inválido");
        }

        // ─── Extrai dados da mensagem ─────────────────────────────────────────
        var message = payload.Data.Message;
        var body = message.Conversation;

        // Extract sender number from Evolution JID format
        // Messages from users: "5511999999999@s.whatsapp.net"
        var fromJid = message.Key?.RemoteJid ?? message.From ?? "";
        var senderNumber = ExtractNumber(fromJid);

        if (string.IsNullOrWhiteSpace(senderNumber) || string.IsNullOrWhiteSpace(body))
        {
            logger.LogWarning("Webhook Evolution com campos ausentes. From={From} Body={Body}", fromJid, body);
            return Results.Ok(); // Retorna 200 para Evolution reconhecer recebimento
        }

        // ─── Resolve o tenant pelo número do webhook (para qual bot vai) ────────
        var tenantNumber = payload.Instance ?? "";
        tenantNumber = ExtractNumber(tenantNumber);

        var tenant = registry.Resolve(tenantNumber);
        if (tenant is null)
        {
            logger.LogWarning("Nenhum tenant encontrado para o número '{TenantNumber}'", tenantNumber);
            return Results.Ok(); // Retorna 200; não há bot para este número
        }

        logger.LogInformation(
            "Mensagem Evolution de {From} para [{Tenant}]: {Body}",
            senderNumber,
            tenant.Config.TenantId,
            body);

        // ─── Processa e responde ──────────────────────────────────────────────
        var reply = await runner.ProcessAsync(tenant, senderNumber, body, ct);

        try
        {
            await messageSender.SendAsync(
                toNumber: senderNumber,
                fromNumber: tenant.Config.WhatsAppNumber,
                body: reply,
                ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao enviar resposta via Evolution API");
            // Continua mesmo se falhar o envio; webhook foi processado
        }

        return Results.Ok();
    }

    /// <summary>Extrai número de um JID do Evolution (ex: "5511999999999@s.whatsapp.net" → "5511999999999")</summary>
    private static string ExtractNumber(string jid)
    {
        if (string.IsNullOrWhiteSpace(jid))
            return "";

        return System.Text.RegularExpressions.Regex.Replace(
            jid.Replace("@s.whatsapp.net", "")
               .Replace("@g.us", ""),
            @"\D",
            "");
    }
}

// ─── Dto classes para desserialização do JSON ──────────────────────────────────
public class EvolutionWebhookPayload
{
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    [JsonPropertyName("instance")]
    public string? Instance { get; set; }

    [JsonPropertyName("data")]
    public EvolutionData? Data { get; set; }
}

public class EvolutionData
{
    [JsonPropertyName("message")]
    public EvolutionMessage? Message { get; set; }
}

public class EvolutionMessage
{
    [JsonPropertyName("key")]
    public EvolutionKey? Key { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("conversation")]
    public string? Conversation { get; set; }

    [JsonPropertyName("fromMe")]
    public bool FromMe { get; set; }

    [JsonPropertyName("participant")]
    public string? Participant { get; set; }
}

public class EvolutionKey
{
    [JsonPropertyName("remoteJid")]
    public string? RemoteJid { get; set; }

    [JsonPropertyName("fromJid")]
    public string? FromJid { get; set; }
}
