using Agiliz.Core.Twilio;

namespace Agiliz.Tests.Fakes;

/// <summary>
/// Sender falso que captura todas as mensagens enviadas.
/// Permite verificar to, from e body sem chamar o Twilio.
/// </summary>
public sealed class FakeTwilioSender : ITwilioSender
{
    public record SentMessage(string To, string From, string Body);

    public List<SentMessage> Sent { get; } = [];

    public Task SendAsync(string toNumber, string fromNumber, string body, CancellationToken ct = default)
    {
        Sent.Add(new SentMessage(toNumber, fromNumber, body));
        return Task.CompletedTask;
    }
}
