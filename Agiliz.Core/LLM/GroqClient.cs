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

    public async Task<LlmResponse> CompleteAsync(
        IReadOnlyList<ConversationMessage> history,
        IReadOnlyList<Agiliz.Core.Tools.ITool>? tools = null,
        CancellationToken ct = default)
    {
        var currentHistory = history.ToList(); // Cópia mutável para o loop
        
        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var toolCosts = new List<ToolExecutionCost>();
        
        while (true)
        {
            var messages = BuildMessages(currentHistory);
            
            var groqTools = tools?.Count > 0 ? tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.ParametersSchema
                }
            }).ToList() : null;

            var body = new
            {
                model = _settings.Model,
                max_tokens = _settings.MaxTokens,
                messages,
                tools = groqTools
            };

            var response = await _http.PostAsJsonAsync(_endpoint, body, JsonOpts, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<GroqResponse>(JsonOpts, ct)
                         ?? throw new InvalidOperationException("Resposta vazia do Groq.");

            var message = result.Choices[0].Message;

            if (result.Usage is not null)
            {
                totalPromptTokens += result.Usage.PromptTokens;
                totalCompletionTokens += result.Usage.CompletionTokens;
            }

            // Se for uma resposta normal de texto
            if (message.ToolCalls is null || message.ToolCalls.Count == 0)
            {
                return new LlmResponse(
                    message.Content ?? "", 
                    new TokenUsage(totalPromptTokens, totalCompletionTokens), 
                    toolCosts);
            }

            // Precisamos executar as tools!
            var toolCallsList = message.ToolCalls.Select(tc => new ToolCall(tc.Id, tc.Function.Name, tc.Function.Arguments)).ToList();
            currentHistory.Add(ConversationMessage.AssistantWithToolCalls(toolCallsList));

            foreach (var tc in message.ToolCalls)
            {
                var tool = tools?.FirstOrDefault(t => t.Name == tc.Function.Name);
                string toolResultStr;
                
                if (tool != null)
                {
                    try
                    {
                        var res = await tool.ExecuteAsync(tc.Function.Arguments, ct);
                        toolResultStr = res.Output;
                        if (res.Cost.HasValue && res.Cost.Value > 0)
                        {
                            toolCosts.Add(new ToolExecutionCost(tool.Name, res.Cost.Value));
                        }
                    }
                    catch (Exception ex)
                    {
                        toolResultStr = "Error executing tool: " + ex.Message;
                    }
                }
                else
                {
                    toolResultStr = "Tool not found.";
                }

                currentHistory.Add(ConversationMessage.ToolResult(tc.Id, toolResultStr));
            }

            // Volta para o topo do while para enviar os resultados das tools de volta para o modelo!
        }
    }

    private List<object> BuildMessages(IReadOnlyList<ConversationMessage> history)
    {
        var list = new List<object>
        {
            new { role = "system", content = _systemPrompt }
        };

        foreach (var msg in history)
        {
            if (msg.Role == MessageRole.User)
            {
                list.Add(new { role = "user", content = msg.Content });
            }
            else if (msg.Role == MessageRole.Assistant)
            {
                if (msg.ToolCalls?.Count > 0)
                {
                    list.Add(new
                    {
                        role = "assistant",
                        tool_calls = msg.ToolCalls.Select(tc => new
                        {
                            id = tc.Id,
                            type = "function",
                            function = new { name = tc.Name, arguments = tc.Arguments }
                        }).ToList()
                    });
                }
                else
                {
                    list.Add(new { role = "assistant", content = msg.Content });
                }
            }
            else if (msg.Role == MessageRole.Tool)
            {
                list.Add(new
                {
                    role = "tool",
                    tool_call_id = msg.ToolCallId,
                    content = msg.Content
                });
            }
        }

        return list;
    }

    // Response DTOs
    private sealed record GroqResponse(List<GroqChoice> Choices, GroqUsage? Usage);
    private sealed record GroqChoice(GroqMessage Message);
    private sealed record GroqMessage(string? Content, List<GroqToolCall>? ToolCalls);
    private sealed record GroqToolCall(string Id, string Type, GroqFunction Function);
    private sealed record GroqFunction(string Name, string Arguments);
    private sealed record GroqUsage(int PromptTokens, int CompletionTokens);
}
