using Agiliz.Core.Messaging;

namespace Agiliz.Tests.Fakes;

/// <summary>
/// Message provider falso que captura todas as mensagens enviadas.
/// Permite verificar to, from e body sem chamar Evolution API.
/// </summary>
public sealed class FakeMessageProvider : IMessageProvider
{
    public record SentMessage(string To, string From, string Body);

    public List<SentMessage> Sent { get; } = [];

    public Task SendAsync(string toNumber, string fromNumber, string body, CancellationToken ct = default)
    {
        Sent.Add(new SentMessage(NormalizeNumber(toNumber), NormalizeNumber(fromNumber), body));
        return Task.CompletedTask;
    }

    public string NormalizeNumber(string number)
    {
        // Remove prefixos e símbolos
        return System.Text.RegularExpressions.Regex.Replace(
            number.Replace("whatsapp:", "")
                   .Replace("@s.whatsapp.net", "")
                   .Replace("@g.us", ""),
            @"\D",
            "");
    }
}
