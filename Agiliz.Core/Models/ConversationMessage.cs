namespace Agiliz.Core.Models;

public sealed class ConversationMessage
{
    public MessageRole Role { get; init; }
    public string Content { get; init; } = string.Empty;

    public static ConversationMessage User(string content) =>
        new() { Role = MessageRole.User, Content = content };

    public static ConversationMessage Assistant(string content) =>
        new() { Role = MessageRole.Assistant, Content = content };
}

public enum MessageRole { User, Assistant }
