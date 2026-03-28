using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Agiliz.Core.Tools;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly ILogger<SmtpEmailSender> _logger;
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _pass;
    private readonly string _from;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger)
    {
        _logger = logger;
        _host = configuration["SMTP_HOST"] ?? "";
        _port = int.TryParse(configuration["SMTP_PORT"], out var p) ? p : 587;
        _user = configuration["SMTP_USER"] ?? "";
        _pass = configuration["SMTP_PASS"] ?? "";
        _from = configuration["SMTP_FROM"] ?? _user;
    }

    public async Task SendEmailAsync(string to, string subject, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_host))
        {
            _logger.LogWarning("MOCK SMTP: Enviando e-mail para {To} | Assunto: {Subject}", to, subject);
            return;
        }

        using var client = new SmtpClient(_host, _port);
        client.Credentials = new NetworkCredential(_user, _pass);
        client.EnableSsl = true;

        using var msg = new MailMessage(_from, to, subject, body);
        
        await client.SendMailAsync(msg, ct);
        _logger.LogInformation("E-mail real enviado via SMTP para {To}", to);
    }
}
