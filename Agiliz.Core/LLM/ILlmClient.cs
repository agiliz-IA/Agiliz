using Agiliz.Core.Models;

namespace Agiliz.Core.LLM;

public interface ILlmClient
{
    /// <summary>
    /// Envia o histórico da sessão e retorna a resposta do LLM, seja texto ou uma série de chamadas de função.
    /// Se o LLM decidir chamar uma ferramenta, o cliente deve resolvê-las automaticamente ou devolver o controle.
    /// Para manter simples, o próprio cliente executará e alimentará o contexto de volta.
    /// Retorna também os custos das ferramentas executadas no pacote de resposta.
    /// </summary>
    Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<Agiliz.Core.Tools.ITool>? tools = null,
        CancellationToken ct = default);
}
