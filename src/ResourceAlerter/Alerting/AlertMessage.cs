namespace ResourceAlerter.Alerting;

public enum AlertKind
{
    ServiceStarted,
    Triggered,
    Reminder,
    Resolved,
    SensorUnavailable,
}

public sealed record AlertMessage
{
    public required AlertKind Kind { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
}
