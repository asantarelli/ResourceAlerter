namespace ResourceAlerter.Alerting;

public enum AlertKind
{
    ServiceStarted,
    Triggered,
    Reminder,
    Resolved,
    SensorUnavailable,
    DailySummary,
}

public sealed record MailAttachment(string FileName, byte[] Content, string MimeType);

public sealed record AlertMessage
{
    public required AlertKind Kind { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public IReadOnlyList<MailAttachment> Attachments { get; init; } = Array.Empty<MailAttachment>();
}
