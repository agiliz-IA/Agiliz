namespace Agiliz.Runtime.Data.Entities;

public class ConversationState
{
    public string Phone { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public bool IsPaused { get; set; }
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PausedAt { get; set; }
    public string? LastMessagePreview { get; set; }
}
