using Twilio;
using Twilio.Rest.Api.V2010.Account;

namespace Agiliz.Core.Twilio;

/// <summary>
/// Envia mensagens via Twilio WhatsApp.
/// Lê TWILIO_ACCOUNT_SID e TWILIO_AUTH_TOKEN das variáveis de ambiente.
/// </summary>
public sealed class TwilioSender : ITwilioSender
{
    public TwilioSender()
    {
        var sid = Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID")
                  ?? throw new InvalidOperationException("TWILIO_ACCOUNT_SID não encontrada.");

        var token = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN")
                    ?? throw new InvalidOperationException("TWILIO_AUTH_TOKEN não encontrada.");

        TwilioClient.Init(sid, token);
    }

    public async Task SendAsync(string toNumber, string fromNumber, string body, CancellationToken ct = default)
    {
        await MessageResource.CreateAsync(
            to: new global::Twilio.Types.PhoneNumber(toNumber),
            from: new global::Twilio.Types.PhoneNumber(fromNumber),
            body: body);
    }
}
