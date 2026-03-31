using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Agiliz.Core.Tools;

public sealed class SendEmailTool : ITool
{
    private readonly ILogger<SendEmailTool> _logger;
    private readonly IEmailSender _emailSender;

    public SendEmailTool(ILogger<SendEmailTool> logger, IEmailSender emailSender)
    {
        _logger = logger;
        _emailSender = emailSender;
    }

    public string Name => "send_email";

    public string Description => "Envia um e-mail para um destinatário. Use esta ferramenta para enviar e-mails de confirmação, relatórios ou notificações.";

    public JsonObject ParametersSchema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["to"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Endereço de e-mail do destinatário."
            },
            ["subject"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Assunto do e-mail."
            },
            ["body"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Corpo do e-mail."
            }
        },
        ["required"] = new JsonArray { "to", "subject", "body" }
    };

    public async Task<Agiliz.Core.Models.ToolResult> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        _logger.LogInformation("LLM chamou SendEmailTool com argumentos: {Args}", arguments);

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var to = doc.RootElement.GetProperty("to").GetString() ?? "";
            var subject = doc.RootElement.GetProperty("subject").GetString() ?? "";
            var body = doc.RootElement.GetProperty("body").GetString() ?? "";

            await _emailSender.SendEmailAsync(to, subject, body, ct);

            return new Agiliz.Core.Models.ToolResult("E-mail enviado com sucesso para " + to, Cost: 0.02m);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao analisar argumentos ou enviar e-mail.");
            return new Agiliz.Core.Models.ToolResult("Falha ao enviar e-mail: " + ex.Message);
        }
    }
}
