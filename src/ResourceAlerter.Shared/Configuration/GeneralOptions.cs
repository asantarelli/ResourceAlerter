namespace ResourceAlerter.Configuration;

public sealed class GeneralOptions
{
    public const string SectionName = "General";

    /// <summary>
    /// Display name used in mail subjects/bodies and log messages (e.g. "[MachineName] ALERT: ...").
    /// Leave empty/null to fall back to the real Windows computer name (<see cref="Environment.MachineName"/>).
    /// This is purely cosmetic — it does NOT affect which appsettings.&lt;MACHINE-NAME&gt;.json
    /// override file gets loaded; that always uses the real OS computer name.
    /// </summary>
    public string? MachineName { get; set; }

    /// <summary>
    /// Language for the Viewer UI and every mail/Discord alert: "es" (Spanish) or "en" (English).
    /// Does NOT affect internal file/console logs, which stay in English regardless (technical
    /// diagnostics, not something sent to end users). See ResourceAlerter.Localization.Strings.
    /// </summary>
    public string Language { get; set; } = "es";
}
