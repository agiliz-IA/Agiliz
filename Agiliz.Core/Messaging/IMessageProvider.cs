namespace Agiliz.Core.Messaging;

/// <summary>
/// Interface genérica para envio de mensagens WhatsApp.
/// Abstrai a implementação específica (Twilio, Evolution, etc).
/// </summary>
public interface IMessageProvider
{
    /// <summary>
    /// Envia uma mensagem WhatsApp.
    /// </summary>
    /// <param name="toNumber">Número do destinatário (ex: "5511999999999")</param>
    /// <param name="fromNumber">Número do remetente (ex: "5511988888888")</param>
    /// <param name="body">Texto da mensagem</param>
    /// <param name="ct">Token de cancelamento</param>
    Task SendAsync(string toNumber, string fromNumber, string body, CancellationToken ct = default);

    /// <summary>Normaliza número para formato padrão (apenas dígitos).</summary>
    string NormalizeNumber(string number);
}
