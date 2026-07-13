using ResourceAlerter.Configuration;

namespace ResourceAlerter.Viewer;

/// <summary>Every settings section the app has, as one unit — what the Settings screen edits.</summary>
public sealed class ConfigBundle
{
    public GeneralOptions General { get; set; } = new();
    public MonitoringOptions Monitoring { get; set; } = new();
    public SmtpOptions Smtp { get; set; } = new();
    public DiscordOptions Discord { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public FileLoggingOptions FileLogging { get; set; } = new();
}
