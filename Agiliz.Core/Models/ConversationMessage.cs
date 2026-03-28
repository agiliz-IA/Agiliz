namespace Agiliz.Core.Models;

public sealed class ConversationMessage
{
    public MessageRole Role { get; init; }
    public string Content { get; init; } = string.Empty;
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }

    public static ConversationMessage User(string content) =>
        new() { Role = MessageRole.User, Content = content };

    public static ConversationMessage Assistant(string content) =>
        new() { Role = MessageRole.Assistant, Content = content };
        
    public static ConversationMessage AssistantWithToolCalls(List<ToolCall> calls) =>
        new() { Role = MessageRole.Assistant, ToolCalls = calls };
        
    public static ConversationMessage ToolResult(string toolCallId, string result) =>
        new() { Role = MessageRole.Tool, ToolCallId = toolCallId, Content = result };
}

public sealed record ToolCall(string Id, string Name, string Arguments);

public enum MessageRole { System, User, Assistant, Tool }
