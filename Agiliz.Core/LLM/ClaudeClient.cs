using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agiliz.Core.Models;

namespace Agiliz.Core.LLM;

public sealed class ClaudeClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly string _systemPrompt;
    private readonly LlmSettings _settings;
    private readonly string _endpoint;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <param name="baseUrl">Override for tests (e.g. WireMock URL). Defaults to https://api.anthropic.com</param>
    public ClaudeClient(HttpClient http, string systemPrompt, LlmSettings settings, string? baseUrl = null)
    {
        _http = http;
        _systemPrompt = systemPrompt;
        _settings = settings;
        _endpoint = $"{(baseUrl ?? "https://api.anthropic.com").TrimEnd('/')}/v1/messages";
    }

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<Agiliz.Core.Tools.ITool>? tools = null,
        CancellationToken ct = default)
    {
        // Mensagens de sistema dinâmicas (data/hora, contexto) vão para o campo "system".
        var systemParts = new List<string> { _systemPrompt };
        systemParts.AddRange(history.Where(m => m.Role == MessageRole.System).Select(m => m.Content));

        var messages = history
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant && !string.IsNullOrEmpty(m.Content))
            .Select(m => new
            {
                role = m.Role == MessageRole.User ? "user" : "assistant",
                content = m.Content
            }).ToList();

        var model = ModelCatalog.Resolve(_settings.Model);

        // Linha 5.x: pensamento adaptativo ligado por padrão e consome max_tokens.
        // Para atendimento (chat curto) usamos effort "low" e um piso de tokens.
        var isGen5 = model.StartsWith("claude-haiku-5", StringComparison.OrdinalIgnoreCase)
                     || model.StartsWith("claude-sonnet-5", StringComparison.OrdinalIgnoreCase)
                     || model.StartsWith("claude-opus-5", StringComparison.OrdinalIgnoreCase);

        var body = new
        {
            model,
            max_tokens = isGen5 ? Math.Max(_settings.MaxTokens, 1024) : _settings.MaxTokens,
            system = string.Join("\n\n", systemParts.Where(s => !string.IsNullOrWhiteSpace(s))),
            messages,
            output_config = isGen5 ? new { effort = "low" } : null
        };

        var response = await _http.PostAsJsonAsync(_endpoint, body, JsonOpts, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClaudeResponse>(JsonOpts, ct)
                     ?? throw new InvalidOperationException("Resposta vazia do Claude.");

        // A resposta pode começar com blocos de "thinking"; pegamos só os blocos de texto.
        var text = string.Concat(result.Content
            .Where(b => string.Equals(b.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(b => b.Text));
        var tokenUsage = new TokenUsage(result.Usage?.InputTokens ?? 0, result.Usage?.OutputTokens ?? 0);
        
        return new LlmResponse(text, tokenUsage, new List<ToolExecutionCost>());
    }

    // Response DTOs
    private sealed record ClaudeResponse(List<ClaudeBlock> Content, ClaudeUsage? Usage);
    private sealed record ClaudeBlock(string Type, string? Text);
    private sealed record ClaudeUsage(int InputTokens, int OutputTokens);
}
