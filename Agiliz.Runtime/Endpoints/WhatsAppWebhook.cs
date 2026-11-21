using Agiliz.Core.Media;
using Agiliz.Core.Messaging;
using Agiliz.Core.Models;
using Agiliz.Runtime.Data;
using Agiliz.Runtime.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

namespace Agiliz.Runtime.Endpoints;

public static class WhatsAppWebhook
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/webhook", HandleAsync)
           .DisableAntiforgery();

        // Rotas usadas pelo painel: mesmo token das rotas /api/admin (podem enviar mensagem em nome do cliente).
        var handoff = app.MapGroup("").AddEndpointFilter<AdminTokenFilter>();

        handoff.MapGet("/api/handoff/paused", async (AgilizDbContext db, CancellationToken ct) =>
        {
            var rows = await db.ConversationStates
                .Where(s => s.IsPaused)
                .OrderByDescending(s => s.PausedAt ?? s.LastActivityAt)
                .Select(s => new { s.TenantId, s.Phone, s.PausedAt, s.LastActivityAt, s.LastMessagePreview })
                .ToListAsync(ct);

            return Results.Ok(rows);
        });

        handoff.MapPost("/api/handoff/{tenantId}/{phone}/resume", (string tenantId, string phone, SessionStore sessions) =>
        {
            sessions.ResumeSession(tenantId, phone);
            return Results.Ok(new { message = $"Sessao retomada para {tenantId}/{phone}" });
        });

        handoff.MapPost("/api/handoff/{tenantId}/{phone}/message", async (
            string tenantId,
            string phone,
            HumanMessageRequest request,
            TenantRegistry registry,
            IMessageProvider messageSender,
            CancellationToken ct) =>
        {
            var tenant = registry.Get(tenantId);
            if (tenant is null) return Results.NotFound(new { error = "Tenant nao encontrado." });
            if (string.IsNullOrWhiteSpace(request.Body)) return Results.BadRequest(new { error = "Mensagem vazia." });

            await messageSender.SendAsync(phone, tenant.Config.WhatsAppNumber, request.Body, ct);
            return Results.Ok();
        });

        handoff.MapPost("/api/unpause/{tenantId}/{phone}", (string tenantId, string phone, SessionStore sessions) =>
        {
            sessions.ResumeSession(tenantId, phone);
            return Results.Ok(new { message = $"Sessao retomada para {tenantId}/{phone}" });
        });
    }

    private static async Task<IResult> HandleAsync(
        EvolutionWebhookPayload? payload,
        TenantRegistry registry,
        BotRunner runner,
        IMessageProvider messageSender,
        IAudioTranscriber transcriber,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("WhatsAppWebhook");

        if (payload?.Data?.Message is null)
        {
            logger.LogWarning("Webhook Evolution recebido com payload invalido");
            return Results.BadRequest("Payload invalido");
        }

        var data = payload.Data;
        var message = data.Message;

        // Evolution v2 manda key/fromMe em data.key; mantemos o formato antigo (data.message.key) por compatibilidade.
        var key = data.Key ?? message.Key;
        if (key?.FromMe == true || message.FromMe)
            return Results.Ok();

        var fromJid = key?.RemoteJid ?? message.From ?? "";

        // Bot não responde em grupos, status nem canais.
        if (IsIgnoredChat(fromJid))
            return Results.Ok();

        // Contas com privacidade LID: o número real vem em senderPn/remoteJidAlt.
        if (fromJid.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
            fromJid = key?.SenderPn ?? key?.RemoteJidAlt ?? fromJid;

        var senderNumber = ExtractNumber(fromJid);

        var tenantNumber = ExtractNumber(payload.Instance ?? "");
        var tenant = registry.Resolve(tenantNumber);
        if (tenant is null)
        {
            logger.LogWarning("Nenhum tenant encontrado para o numero '{TenantNumber}'", tenantNumber);
            return Results.Ok();
        }

        var body = ExtractText(message);

        if (string.IsNullOrWhiteSpace(body) && message.AudioMessage is not null)
            body = await TranscribeAudioAsync(payload, key, message, tenant, messageSender, transcriber, runner, logger, ct);

        if (string.IsNullOrWhiteSpace(body))
            body = DescribeMediaWithoutText(message);

        if (string.IsNullOrWhiteSpace(senderNumber) || string.IsNullOrWhiteSpace(body))
        {
            logger.LogWarning("Webhook Evolution com campos ausentes. From={From} Body={HasBody} Type={Type}",
                fromJid, !string.IsNullOrWhiteSpace(body), data.MessageType);
            return Results.Ok();
        }

        logger.LogInformation("Mensagem Evolution de {From} para [{Tenant}]", senderNumber, tenant.Config.TenantId);

        var reply = await runner.ProcessAsync(tenant, senderNumber, body, ct);
        if (reply is not null)
        {
            try
            {
                var buttonMatches = System.Text.RegularExpressions.Regex.Matches(reply, @"\[btn:(.+?)\]");
                if (buttonMatches.Count > 0)
                {
                    var buttons = buttonMatches.Select(m => m.Groups[1].Value).ToArray();
                    var cleanReply = System.Text.RegularExpressions.Regex.Replace(reply, @"\[btn:(.+?)\]", "").Trim();

                    await messageSender.SendButtonsAsync(senderNumber, tenant.Config.WhatsAppNumber, cleanReply, buttons, ct);
                }
                else
                {
                    await messageSender.SendAsync(senderNumber, tenant.Config.WhatsAppNumber, reply, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao enviar resposta via Evolution API");
            }
        }

        return Results.Ok();
    }

    /// <summary>Texto da mensagem nos vários formatos do WhatsApp (texto simples, resposta citada, legenda, botões, listas).</summary>
    internal static string? ExtractText(EvolutionMessage message) =>
        FirstNonEmpty(
            message.Conversation,
            message.ExtendedTextMessage?.Text,
            message.ImageMessage?.Caption,
            message.VideoMessage?.Caption,
            message.DocumentMessage?.Caption,
            message.ButtonsResponseMessage?.SelectedDisplayText,
            message.ButtonResponseMessage?.SelectedDisplayText,
            message.ButtonResponseMessage?.SelectedButtonId,
            message.TemplateButtonReplyMessage?.SelectedDisplayText,
            message.ListResponseMessage?.Title,
            message.ListResponseMessage?.SingleSelectReply?.SelectedRowId);

    private static string? DescribeMediaWithoutText(EvolutionMessage message)
    {
        if (message.ImageMessage is not null) return "[O cliente enviou uma imagem sem legenda]";
        if (message.DocumentMessage is not null) return "[O cliente enviou um documento]";
        if (message.VideoMessage is not null) return "[O cliente enviou um vídeo sem legenda]";
        return null; // figurinhas, reações etc. são ignoradas
    }

    private static async Task<string?> TranscribeAudioAsync(
        EvolutionWebhookPayload payload,
        EvolutionKey? key,
        EvolutionMessage message,
        TenantEntry tenant,
        IMessageProvider messageSender,
        IAudioTranscriber transcriber,
        BotRunner runner,
        ILogger logger,
        CancellationToken ct)
    {
        const string NotTranscribed = "[O cliente enviou um áudio que não pôde ser ouvido. Peça gentilmente para ele escrever a mensagem.]";

        if (!transcriber.IsEnabled)
            return NotTranscribed;

        try
        {
            MediaContent? audio = null;

            // Com webhookBase64 ligado na Evolution o áudio já vem no payload; senão baixamos pela API.
            if (!string.IsNullOrWhiteSpace(message.Base64))
                audio = new MediaContent(Convert.FromBase64String(message.Base64), message.AudioMessage?.Mimetype ?? "audio/ogg");
            else if (!string.IsNullOrWhiteSpace(key?.Id) && !string.IsNullOrWhiteSpace(payload.Instance))
                audio = await messageSender.DownloadMediaAsync(payload.Instance, key.Id, ct);

            if (audio is null)
                return NotTranscribed;

            var text = await transcriber.TranscribeAsync(audio, ct);
            if (string.IsNullOrWhiteSpace(text))
                return NotTranscribed;

            var seconds = message.AudioMessage?.Seconds ?? 30;
            runner.RecordCost(tenant.Config.TenantId, CostType.ToolExecution, $"Transcrição de áudio ({seconds}s)",
                Agiliz.Core.LLM.ModelCatalog.EstimateTranscriptionCostUsd(seconds));

            logger.LogInformation("[{Tenant}] Áudio de {Seconds}s transcrito", tenant.Config.TenantId, seconds);
            return $"[Áudio transcrito] {text}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Tenant}] Falha ao transcrever áudio", tenant.Config.TenantId);
            return NotTranscribed;
        }
    }

    private static bool IsIgnoredChat(string jid) =>
        jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase) ||
        jid.EndsWith("@broadcast", StringComparison.OrdinalIgnoreCase) ||
        jid.EndsWith("@newsletter", StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string ExtractNumber(string jid)
    {
        if (string.IsNullOrWhiteSpace(jid)) return "";

        // Remove sufixo (@s.whatsapp.net, @lid...) e device id (":12")
        var user = jid.Split('@')[0].Split(':')[0];
        return System.Text.RegularExpressions.Regex.Replace(user, @"\D", "");
    }
}

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
    [JsonPropertyName("key")]
    public EvolutionKey? Key { get; set; }

    [JsonPropertyName("pushName")]
    public string? PushName { get; set; }

    [JsonPropertyName("messageType")]
    public string? MessageType { get; set; }

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

    [JsonPropertyName("buttonResponseMessage")]
    public EvolutionButtonResponse? ButtonResponseMessage { get; set; }

    [JsonPropertyName("buttonsResponseMessage")]
    public EvolutionButtonResponse? ButtonsResponseMessage { get; set; }

    [JsonPropertyName("templateButtonReplyMessage")]
    public EvolutionButtonResponse? TemplateButtonReplyMessage { get; set; }

    [JsonPropertyName("listResponseMessage")]
    public EvolutionListResponse? ListResponseMessage { get; set; }

    [JsonPropertyName("extendedTextMessage")]
    public EvolutionTextHolder? ExtendedTextMessage { get; set; }

    [JsonPropertyName("imageMessage")]
    public EvolutionMediaMessage? ImageMessage { get; set; }

    [JsonPropertyName("videoMessage")]
    public EvolutionMediaMessage? VideoMessage { get; set; }

    [JsonPropertyName("documentMessage")]
    public EvolutionMediaMessage? DocumentMessage { get; set; }

    [JsonPropertyName("audioMessage")]
    public EvolutionMediaMessage? AudioMessage { get; set; }

    /// <summary>Mídia em base64 (quando o webhook da Evolution tem webhookBase64 = true).</summary>
    [JsonPropertyName("base64")]
    public string? Base64 { get; set; }

    [JsonPropertyName("fromMe")]
    public bool FromMe { get; set; }

    [JsonPropertyName("participant")]
    public string? Participant { get; set; }
}

public class EvolutionButtonResponse
{
    [JsonPropertyName("selectedButtonId")]
    public string? SelectedButtonId { get; set; }

    [JsonPropertyName("selectedDisplayText")]
    public string? SelectedDisplayText { get; set; }
}

public class EvolutionTextHolder
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

public class EvolutionMediaMessage
{
    [JsonPropertyName("caption")]
    public string? Caption { get; set; }

    [JsonPropertyName("mimetype")]
    public string? Mimetype { get; set; }

    [JsonPropertyName("seconds")]
    public int? Seconds { get; set; }
}

public class EvolutionListResponse
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("singleSelectReply")]
    public EvolutionSingleSelectReply? SingleSelectReply { get; set; }
}

public class EvolutionSingleSelectReply
{
    [JsonPropertyName("selectedRowId")]
    public string? SelectedRowId { get; set; }
}

public class EvolutionKey
{
    [JsonPropertyName("remoteJid")]
    public string? RemoteJid { get; set; }

    [JsonPropertyName("remoteJidAlt")]
    public string? RemoteJidAlt { get; set; }

    [JsonPropertyName("senderPn")]
    public string? SenderPn { get; set; }

    [JsonPropertyName("fromMe")]
    public bool FromMe { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("fromJid")]
    public string? FromJid { get; set; }
}

public class HumanMessageRequest
{
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;
}
