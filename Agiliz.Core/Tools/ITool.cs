using System.Text.Json.Nodes;

namespace Agiliz.Core.Tools;

/// <summary>
/// Representa uma habilidade (Skill/Tool) que o LLM pode invocar.
/// </summary>
public interface ITool
{
    /// <summary>Nome da ferramenta (apenas caracteres alfanumericos e underscores).</summary>
    string Name { get; }

    /// <summary>Descrição clara para o LLM entender quando usar esta ferramenta.</summary>
    string Description { get; }

    /// <summary>JSON Schema dos parâmetros. Geralmente um type: object com propriedades.</summary>
    JsonObject ParametersSchema { get; }

    /// <summary>Executa a ferramenta com a string JSON gerada pelo LLM e retorna o resultado.</summary>
    Task<Agiliz.Core.Models.ToolResult> ExecuteAsync(string arguments, CancellationToken ct = default);
}
