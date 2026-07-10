namespace ResourceAlerter.Configuration;

public sealed class FileLoggingOptions
{
    public const string SectionName = "FileLogging";

    /// <summary>Directory to write log files into. Relative paths are resolved against the app's base directory.</summary>
    public string Directory { get; set; } = "logs";

    /// <summary>Log files roll to a new file once they exceed this size.</summary>
    public int MaxFileSizeMb { get; set; } = 10;

    /// <summary>Log files (by day) older than this are deleted on startup and at midnight rollover.</summary>
    public int RetentionDays { get; set; } = 30;
}
