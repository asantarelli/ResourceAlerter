namespace ResourceAlerter.Monitors;

/// <summary>
/// Minimal read-only INI parser — just enough to read a `[Section]` of `Key=Value` lines out of
/// a Windows-style INI file (e.g. a Clarion app's own connection-settings file). No external
/// dependency needed for something this small, consistent with this project's general "avoid
/// heavy dependencies" stance.
/// </summary>
public static class IniFile
{
    /// <summary>
    /// Reads one section's key/value pairs. Comment lines (`;`/`#`) and blank lines are ignored.
    /// Keys are case-insensitive, matching how Windows INI files are conventionally read.
    /// Returns an empty dictionary (not an exception) if the file or section doesn't exist —
    /// callers report that as a normal "unavailable" condition, not a crash.
    /// </summary>
    public static Dictionary<string, string> ReadSection(string path, string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        string? currentSection = null;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            if (currentSection is null || !string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }
}
