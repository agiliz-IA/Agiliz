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

    /// <summary>Resposta do teste de conexão do WhatsApp.</summary>
    public Agiliz.Core.Integrations.IntegrationTestResult ConnectionResult { get; set; } = new(true, "WhatsApp conectado (fake).");

    public List<string> ConnectionChecks { get; } = [];

    /// <summary>Resposta do código de pareamento (conectar o número pelo celular).</summary>
    public Agiliz.Core.Messaging.WhatsAppPairingResult PairingResult { get; set; } = new(true, "ABCD1234", "ok (fake)");

    public Task<Agiliz.Core.Messaging.WhatsAppPairingResult> RequestPairingCodeAsync(string instance, string phoneNumber, CancellationToken ct = default) =>
        Task.FromResult(PairingResult);

    public Task<Agiliz.Core.Integrations.IntegrationTestResult> CheckConnectionAsync(string instance, CancellationToken ct = default)
    {
        ConnectionChecks.Add(instance);
        return Task.FromResult(ConnectionResult);
    }

    public Task SendAsync(string toNumber, string fromNumber, string body, CancellationToken ct = default)
    {
        Sent.Add(new SentMessage(NormalizeNumber(toNumber), NormalizeNumber(fromNumber), body));
        return Task.CompletedTask;
    }

    public Task SendButtonsAsync(string toNumber, string fromNumber, string body, string[] buttons, CancellationToken ct = default)
    {
        var btnText = string.Join(", ", buttons);
        Sent.Add(new SentMessage(NormalizeNumber(toNumber), NormalizeNumber(fromNumber), $"{body} [Buttons: {btnText}]"));
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
