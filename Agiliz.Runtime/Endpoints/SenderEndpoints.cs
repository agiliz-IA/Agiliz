using Agiliz.Core.Messaging;
using Agiliz.Runtime.Services;

namespace Agiliz.Runtime.Endpoints;

/// <summary>
/// Número oficial da Agiliz (Portal:SenderNumber): de onde saem códigos de acesso e convites.
/// Assim o código chega mesmo com o bot do cliente desconectado, ou quando a demonstração
/// está no próprio celular de quem testa (o código iria de você para você e passaria despercebido).
/// </summary>
public static class SenderEndpoints
{
    public sealed record NotifyRequest(string? Phone, string? Text, string? TenantId);

    public static void Map(WebApplication app)
    {
        var sender = app.MapGroup("/api/admin/sender")
            .AddEndpointFilter<AdminTokenFilter>()
            .DisableAntiforgery();

        sender.MapPost("/notify", NotifyAsync);
        sender.MapGet("/status", async (IConfiguration config, IMessageProvider messages, CancellationToken ct) =>
            SenderNumber(config) is { } number
                ? Results.Ok(await messages.CheckConnectionAsync(messages.NormalizeNumber(number), ct))
                : Results.Ok(new { ok = false, message = "Número oficial não configurado (PORTAL_SENDER_NUMBER)." }));
        sender.MapPost("/pairing", async (IConfiguration config, IMessageProvider messages, CancellationToken ct) =>
            SenderNumber(config) is { } number
                ? Results.Ok(await messages.RequestPairingCodeAsync(messages.NormalizeNumber(number), number, ct))
                : Results.Ok(new WhatsAppPairingResult(false, null, "Número oficial não configurado (PORTAL_SENDER_NUMBER).")));
    }

    public static string? SenderNumber(IConfiguration config) =>
        string.IsNullOrWhiteSpace(config["Portal:SenderNumber"]) ? null : config["Portal:SenderNumber"];

    /// <summary>
    /// Manda um texto pelo número oficial; se ele não estiver configurado ou falhar, pelo número do bot
    /// (<c>tenantId</c>), quando informado. 204 = enviado; 502 = nenhum dos dois conseguiu.
    /// </summary>
    private static async Task<IResult> NotifyAsync(NotifyRequest request, IConfiguration config, TenantRegistry registry,
        IMessageProvider messages, ILoggerFactory loggers, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Phone) || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 2000)
            return Results.BadRequest(new { error = "Telefone e texto são obrigatórios." });

        var logger = loggers.CreateLogger("Sender");
        var senders = new List<string>();
        if (SenderNumber(config) is { } central) senders.Add(central);
        if (request.TenantId is { Length: > 0 } tenantId && registry.Get(tenantId) is { } tenant) senders.Add(tenant.Config.WhatsAppNumber);

        foreach (var from in senders.Distinct())
        {
            if (PhoneNumbers.Matches(from, request.Phone))
                continue; // mandar para si mesmo cai na conversa "Você", sem notificação

            try
            {
                await messages.SendAsync(request.Phone, from, request.Text, ct);
                return Results.NoContent();
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                logger.LogWarning(ex, "Mensagem de acesso não saiu do número {From}", Mask(from));
            }
        }

        if (senders.Count == 0)
            logger.LogWarning("Mensagem de acesso sem remetente: configure Portal:SenderNumber (número oficial da Agiliz)");
        return Results.Problem("Não foi possível enviar pelo WhatsApp.", statusCode: StatusCodes.Status502BadGateway);
    }

    private static string Mask(string phone) => phone.Length <= 4 ? "••••" : new string('•', phone.Length - 4) + phone[^4..];
}
