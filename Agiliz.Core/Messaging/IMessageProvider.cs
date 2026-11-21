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

    /// <summary>
    /// Envia uma mensagem com botões interativos.
    /// </summary>
    Task SendButtonsAsync(string toNumber, string fromNumber, string body, string[] buttons, CancellationToken ct = default);

    /// <summary>Normaliza número para formato padrão (apenas dígitos).</summary>
    string NormalizeNumber(string number);

    /// <summary>
    /// Baixa a mídia (áudio, imagem...) de uma mensagem recebida.
    /// Retorna null quando o provedor não suporta ou a mídia não está mais disponível.
    /// </summary>
    Task<Agiliz.Core.Media.MediaContent?> DownloadMediaAsync(string instance, string messageId, CancellationToken ct = default) =>
        Task.FromResult<Agiliz.Core.Media.MediaContent?>(null);

    /// <summary>Confere se o número do bot está conectado (botão "Testar conexão").</summary>
    Task<Agiliz.Core.Integrations.IntegrationTestResult> CheckConnectionAsync(string instance, CancellationToken ct = default) =>
        Task.FromResult(new Agiliz.Core.Integrations.IntegrationTestResult(false, "Este provedor de mensagens não tem teste de conexão."));
}
