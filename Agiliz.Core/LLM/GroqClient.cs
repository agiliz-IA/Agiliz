using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agiliz.Core.Models;

namespace Agiliz.Core.LLM;

public sealed class GroqClient : ILlmClient
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

    /// <param name="baseUrl">Override for tests (e.g. WireMock URL). Defaults to https://api.groq.com</param>
    public GroqClient(HttpClient http, string systemPrompt, LlmSettings settings, string? baseUrl = null)
    {
        _http = http;
        _systemPrompt = systemPrompt;
        _settings = settings;
        _endpoint = $"{(baseUrl ?? "https://api.groq.com").TrimEnd('/')}/openai/v1/chat/completions";
    }

    public async Task<string> CompleteAsync(
        IReadOnlyList<ConversationMessage> history,
        CancellationToken ct = default)
    {
        var messages = BuildMessages(history);
        var body = new
        {
            model = _settings.Model,
            max_tokens = _settings.MaxTokens,
            messages
        };

        var response = await _http.PostAsJsonAsync(_endpoint, body, JsonOpts, ct);

        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<GroqResponse>(JsonOpts, ct)
                     ?? throw new InvalidOperationException("Resposta vazia do Groq.");

        return result.Choices[0].Message.Content;
    }

    private List<object> BuildMessages(IReadOnlyList<ConversationMessage> history)
    {
        var list = new List<object>
        {
            new { role = "system", content = _systemPrompt }
        };

        foreach (var msg in history)
            list.Add(new
            {
                role = msg.Role == MessageRole.User ? "user" : "assistant",
                content = msg.Content
            });

        return list;
    }

    // Response DTOs
    private sealed record GroqResponse(List<GroqChoice> Choices);
    private sealed record GroqChoice(GroqMessage Message);
    private sealed record GroqMessage(string Content);
}
