using Agiliz.Core.LLM;
using Agiliz.Core.Models;

namespace Agiliz.Tests.Fakes;

/// <summary>
/// Cliente LLM falso com comportamento configurável por teste.
/// Pode retornar respostas fixas, lançar exceções ou capturar o histórico recebido.
/// </summary>
public sealed class FakeLlmClient : ILlmClient
{
    private readonly Queue<string> _replies = new();
    private Exception? _exceptionToThrow;

    public List<IReadOnlyList<ConversationMessage>> ReceivedHistories { get; } = [];
    public int CallCount => ReceivedHistories.Count;

    /// <summary>Enfileira respostas que serão retornadas em sequência.</summary>
    public FakeLlmClient Returns(params string[] replies)
    {
        foreach (var r in replies) _replies.Enqueue(r);
        return this;
    }

    /// <summary>Configura o cliente para lançar exceção na próxima chamada.</summary>
    public FakeLlmClient Throws(Exception ex)
    {
        _exceptionToThrow = ex;
        return this;
    }

    public Task<LlmResponse> CompleteAsync(IReadOnlyList<ConversationMessage> history, IReadOnlyList<Agiliz.Core.Tools.ITool>? tools = null, CancellationToken ct = default)
    {
        ReceivedHistories.Add(history);

        if (_exceptionToThrow is not null)
        {
            var ex = _exceptionToThrow;
            _exceptionToThrow = null;
            throw ex;
        }

        if (_replies.TryDequeue(out var reply))
            return Task.FromResult(new LlmResponse(reply, new TokenUsage(10, 20), new List<ToolExecutionCost>()));

        return Task.FromResult(new LlmResponse("Resposta padrão do fake LLM.", new TokenUsage(10, 20), new List<ToolExecutionCost>()));
    }
}
