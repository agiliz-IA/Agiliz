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
        var messages = history.Select(m => new
        {
            role = m.Role == MessageRole.User ? "user" : "assistant",
            content = m.Content
        }).ToList();

        var body = new
        {
            model = _settings.Model,
            max_tokens = _settings.MaxTokens,
            system = _systemPrompt,
            messages
        };

        var response = await _http.PostAsJsonAsync(_endpoint, body, JsonOpts, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClaudeResponse>(JsonOpts, ct)
                     ?? throw new InvalidOperationException("Resposta vazia do Claude.");

        var text = result.Content[0].Text;
        var tokenUsage = new TokenUsage(result.Usage?.InputTokens ?? 0, result.Usage?.OutputTokens ?? 0);
        
        return new LlmResponse(text, tokenUsage, new List<ToolExecutionCost>());
    }

    // Response DTOs
    private sealed record ClaudeResponse(List<ClaudeBlock> Content, ClaudeUsage? Usage);
    private sealed record ClaudeBlock(string Type, string Text);
    private sealed record ClaudeUsage(int InputTokens, int OutputTokens);
}
