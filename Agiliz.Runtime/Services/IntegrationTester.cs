using Agiliz.Core.Integrations;
using Agiliz.Core.Media;
using Agiliz.Core.Messaging;
using Agiliz.Core.Models;
using Agiliz.Core.Tools;

namespace Agiliz.Runtime.Services;

/// <summary>
/// Botão "Testar conexão" do painel. Roda no Runtime porque é ele que tem as credenciais
/// (Google, Evolution, Groq, SMTP). Nunca lança exceção: devolve a mensagem de erro em português.
/// </summary>
public sealed class IntegrationTester(
    IServiceProvider services,
    IMessageProvider messages,
    IAudioTranscriber transcriber,
    IEmailSender email,
    ILogger<IntegrationTester> logger)
{
    public async Task<IntegrationTestResult> TestAsync(
        string integrationId,
        BotConfig config,
        IReadOnlyDictionary<string, string>? fields,
        CancellationToken ct = default)
    {
        var def = IntegrationCatalog.Find(integrationId);
        if (def is null) return new(false, "Integração fora do catálogo.");
        if (!def.Testable) return new(false, $"{def.Name} não tem teste de conexão.");

        try
        {
            return def.Id switch
            {
                IntegrationCatalog.GoogleCalendar => await TestCalendarAsync(config, fields, ct),
                IntegrationCatalog.WhatsApp => await messages.CheckConnectionAsync(config.WhatsAppNumber, ct),
                IntegrationCatalog.Audio => await transcriber.CheckAsync(ct),
                IntegrationCatalog.Email => await TestEmailAsync(config, ct),
                _ => new(false, $"{def.Name} não tem teste de conexão.")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[{Tenant}] Teste de conexão de {Integration} falhou", config.TenantId, integrationId);
            return new(false, $"Erro inesperado no teste: {ex.Message}");
        }
    }

    public IntegrationServerInfo Info()
    {
        var calendar = TryGetCalendar();
        return new IntegrationServerInfo
        {
            GoogleConfigured = calendar?.IsConfigured == true,
            GoogleServiceAccountEmail = calendar?.ServiceAccountEmail,
            AudioConfigured = transcriber.IsEnabled,
            SmtpConfigured = email.IsConfigured
        };
    }

    private async Task<IntegrationTestResult> TestCalendarAsync(BotConfig config, IReadOnlyDictionary<string, string>? fields, CancellationToken ct)
    {
        var calendarId = fields?.GetValueOrDefault(IntegrationCatalog.CalendarIdField)?.Trim();
        if (string.IsNullOrWhiteSpace(calendarId)) calendarId = BotIntegrations.CalendarId(config);
        if (string.IsNullOrWhiteSpace(calendarId)) return new(false, "Preencha o ID da agenda.");

        var calendar = TryGetCalendar();
        return calendar is null
            ? new(false, "Credenciais do Google ausentes no servidor (configs/google_credentials.json).")
            : await calendar.CheckAccessAsync(calendarId, ct);
    }

    private async Task<IntegrationTestResult> TestEmailAsync(BotConfig config, CancellationToken ct)
    {
        if (!email.IsConfigured)
            return new(false, "SMTP não configurado no servidor (SMTP_HOST vazio): os e-mails só aparecem no log.");
        if (string.IsNullOrWhiteSpace(email.FromAddress))
            return new(false, "SMTP sem remetente (SMTP_FROM ou SMTP_USER).");

        await email.SendEmailAsync(email.FromAddress, "Teste de conexão Agiliz",
            $"E-mail de teste do bot {config.TenantId}, enviado pelo botão \"Testar conexão\" do painel.", ct);
        return new(true, $"E-mail de teste enviado para {email.FromAddress}.");
    }

    /// <summary>Em produção o serviço lança exceção sem credenciais; aqui isso vira "não configurado".</summary>
    private GoogleCalendarService? TryGetCalendar()
    {
        try { return services.GetService<GoogleCalendarService>(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Google Calendar indisponível");
            return null;
        }
    }
}
