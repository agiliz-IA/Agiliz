using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agiliz.Core.Models;

namespace Agiliz.Core.LLM;

public sealed class GroqClient(HttpClient http, string systemPrompt, LlmSettings settings) : ILlmClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<string> CompleteAsync(
        IReadOnlyList<ConversationMessage> history,
        CancellationToken ct = default)
    {
        var messages = BuildMessages(history);
        var body = new
        {
            model = settings.Model,
            max_tokens = settings.MaxTokens,
            messages
        };

        var response = await http.PostAsJsonAsync(
            "https://api.groq.com/openai/v1/chat/completions",
            body, JsonOpts, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GroqResponse>(JsonOpts, ct)
                     ?? throw new InvalidOperationException("Resposta vazia do Groq.");

        return result.Choices[0].Message.Content;
    }

    private List<object> BuildMessages(IReadOnlyList<ConversationMessage> history)
    {
        var list = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        foreach (var msg in history)
            list.Add(new
            {
                role = msg.Role == MessageRole.User ? "user" : "assistant",
                content = msg.Content
            });

        return list;
    }

    // ─── Response DTOs ───────────────────────────────────────────────────────

    private sealed record GroqResponse(List<GroqChoice> Choices);
    private sealed record GroqChoice(GroqMessage Message);
    private sealed record GroqMessage(string Content);
}
