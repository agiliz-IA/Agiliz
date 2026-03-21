using Agiliz.Core.Models;

namespace Agiliz.Core.LLM;

public interface ILlmClient
{
    /// <summary>
    /// Envia o histórico da sessão e retorna a resposta do LLM.
    /// O system prompt já vem embutido nas configurações do cliente.
    /// </summary>
    Task<string> CompleteAsync(
        IReadOnlyList<ConversationMessage> history,
        CancellationToken ct = default);
}
