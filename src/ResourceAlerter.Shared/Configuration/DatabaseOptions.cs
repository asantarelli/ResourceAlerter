namespace ResourceAlerter.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Path to the SQLite database file. Environment variables are expanded. The default lives
    /// under ProgramData (not Program Files) so the Viewer, running as a regular user, can
    /// read it while the service (LocalSystem) writes it.
    /// </summary>
    public string Path { get; set; } = @"%ProgramData%\ResourceAlerter\resourcealerter.db";

    /// <summary>Samples and alert events older than this are purged once a day.</summary>
    public int RetentionDays { get; set; } = 90;

    public string GetExpandedPath() => Environment.ExpandEnvironmentVariables(Path);
}
