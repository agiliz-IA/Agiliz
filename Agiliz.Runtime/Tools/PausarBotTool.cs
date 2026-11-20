using System.Text.Json.Nodes;
using Agiliz.Core.Tools;
using Agiliz.Core.Models;
using Agiliz.Runtime.Services;

namespace Agiliz.Runtime.Tools;

public sealed class PausarBotTool(SessionStore sessions) : ITool
{
    public string Name => "chamar_atendente";
    public string Description => "Pausa o bot e transfere a conversa para um atendente humano. Use essa ferramenta se o cliente fizer perguntas complexas que você não sabe responder, ou se ele pedir explicitamente para falar com um humano.";
    
    public JsonObject ParametersSchema => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject()
    };

    public Task<ToolResult> ExecuteAsync(string arguments, CancellationToken ct = default)

    {
        var ctx = RuntimeContext.Current.Value;
        if (ctx == null) return Task.FromResult(new ToolResult("Erro interno: Contexto do usuário não encontrado."));

        sessions.PauseSession(ctx.TenantId, ctx.UserPhone);

        // A mensagem de retorno do LLM para o usuário será enviada. A sessão ficará pausada a partir da PRÓXIMA mensagem do usuário.
        return Task.FromResult(new ToolResult("O bot foi pausado com sucesso. O atendente humano assumirá em breve. Opcional: Diga ao usuário que você está transferindo o atendimento e peça que ele aguarde um momento."));
    }
}
