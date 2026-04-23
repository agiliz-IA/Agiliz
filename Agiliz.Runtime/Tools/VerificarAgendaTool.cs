using System.Text.Json.Nodes;
using Agiliz.Core.Tools;
using Agiliz.Core.Models;

namespace Agiliz.Runtime.Tools;

public sealed class VerificarAgendaTool : ITool
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
        await Task.Delay(100, ct);
        return new ToolResult($@"{{ ""disponivel"": [""09:00"", ""10:00"", ""14:00"", ""15:30""] }}");
    }
}
