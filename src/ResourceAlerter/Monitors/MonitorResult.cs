namespace ResourceAlerter.Monitors;

/// <summary>Outcome of a single health check sample.</summary>
public sealed record MonitorResult
{
    /// <summary>Human-readable label for the specific thing being measured (e.g. "CPU Package", "+12V", "C:").</summary>
    public required string Subject { get; init; }

    public required bool InRange { get; init; }

    /// <summary>Current value, formatted for display (e.g. "87.3%", "82.1°C").</summary>
    public required string DisplayValue { get; init; }

    /// <summary>Configured threshold, formatted for display.</summary>
    public required string DisplayThreshold { get; init; }

    /// <summary>Set when the monitor could not take a reading at all (missing sensor, etc.). Never triggers an alert.</summary>
    public bool Unavailable { get; init; }

    public string? UnavailableReason { get; init; }
}
