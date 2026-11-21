namespace Agiliz.Core.Tools;

public interface IEmailSender
{
    Task SendEmailAsync(string to, string subject, string body, CancellationToken ct = default);

    /// <summary>False = modo simulação (e-mails só no log).</summary>
    bool IsConfigured => true;

    /// <summary>Remetente configurado; o teste de conexão envia para ele mesmo.</summary>
    string? FromAddress => null;
}
