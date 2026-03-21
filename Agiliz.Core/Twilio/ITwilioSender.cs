namespace Agiliz.Core.Twilio;

public interface ITwilioSender
{
    /// <summary>Envia uma mensagem WhatsApp de volta ao usuário.</summary>
    Task SendAsync(string toNumber, string fromNumber, string body, CancellationToken ct = default);
}
