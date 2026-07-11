using System.Reflection;

namespace ResourceAlerter;

/// <summary>Build/version info, read from the assembly (set via `-p:Version=X.Y.Z` at publish time).</summary>
public static class AppInfo
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
}
