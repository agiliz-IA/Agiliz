using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agiliz.Core.Models;

namespace Agiliz.Core.LLM;

public sealed class ClaudeClient(HttpClient http, string systemPrompt, LlmSettings settings) : ILlmClient
{
    private const string ApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> CompleteAsync(
        IReadOnlyList<ConversationMessage> history,
        CancellationToken ct = default)
    {
        var messages = history.Select(m => new
        {
            role = m.Role == MessageRole.User ? "user" : "assistant",
            content = m.Content
        }).ToList();

        var body = new
        {
            model = settings.Model,
            max_tokens = settings.MaxTokens,
            system = systemPrompt,
            messages
        };

        var response = await http.PostAsJsonAsync(
            "https://api.anthropic.com/v1/messages",
            body, JsonOpts, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ClaudeResponse>(JsonOpts, ct)
                     ?? throw new InvalidOperationException("Resposta vazia do Claude.");

        return result.Content[0].Text;
    }

    // ─── Response DTOs ───────────────────────────────────────────────────────

    private sealed record ClaudeResponse(List<ClaudeBlock> Content);
    private sealed record ClaudeBlock(string Type, string Text);
}
