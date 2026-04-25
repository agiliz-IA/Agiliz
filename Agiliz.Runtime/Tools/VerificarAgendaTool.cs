using System.Text.Json;
using System.Text.Json.Nodes;
using Agiliz.Core.Tools;
using Agiliz.Core.Models;
using Agiliz.Runtime.Services;

namespace Agiliz.Runtime.Tools;

public sealed class VerificarAgendaTool(GoogleCalendarService calendarService, TenantRegistry registry) : ITool
{
    public string Name => "verificar_agenda";
    public string Description => "Verifica os horários disponíveis para agendamento em uma data específica.";
    
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Data para verificação no formato AAAA-MM-DD"
            }
        },
        ["required"] = new JsonArray { "data" }
    };

    public async Task<ToolResult> ExecuteAsync(string arguments, CancellationToken ct = default)
    {
        var ctx = RuntimeContext.Current.Value;
        if (ctx == null) return new ToolResult("Erro interno: Contexto do usuário não encontrado.");

        var tenant = registry.Get(ctx.TenantId);
        var googleCalendarId = tenant?.Config.GoogleCalendarId;

        if (string.IsNullOrEmpty(googleCalendarId))
        {
            return new ToolResult("O calendário Google não está configurado para esta clínica. Avise o paciente.");
        }

        using var doc = JsonDocument.Parse(arguments);
        var dataStr = doc.RootElement.GetProperty("data").GetString() ?? "";

        if (!DateTimeOffset.TryParse(dataStr, out var data))
            return new ToolResult("Formato de data inválido. Use AAAA-MM-DD.");

        var livres = await calendarService.GetAvailableSlotsAsync(googleCalendarId, data);
        
        var jsonResponse = JsonSerializer.Serialize(new { data = dataStr, disponivel = livres });
        return new ToolResult(jsonResponse);
    }
}
