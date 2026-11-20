namespace Agiliz.Runtime.Data.Entities;

public enum AppointmentStatus
{
    Pending,
    Confirmed,
    Cancelled,
    NoShow
}

public class Appointment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Phone { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public DateTimeOffset ScheduledTime { get; set; }
    public int DurationMinutes { get; set; } = 60;
    public AppointmentStatus Status { get; set; } = AppointmentStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReminderSentAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? GoogleEventId { get; set; }
}
