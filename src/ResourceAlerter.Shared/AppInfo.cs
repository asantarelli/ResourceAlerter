using System.Reflection;

namespace ResourceAlerter;

/// <summary>
/// Build/version info, read from the calling assembly (set via `-p:Version=X.Y.Z` at publish
/// time — build-installer.ps1 passes this to both the service and the Viewer, so each reports
/// its own actual version, not a hardcoded string that would drift out of sync on the next
/// release).
/// </summary>
public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown";
}
