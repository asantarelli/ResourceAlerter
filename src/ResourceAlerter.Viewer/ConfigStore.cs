using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ResourceAlerter.Configuration;

namespace ResourceAlerter.Viewer;

/// <summary>
/// Reads/writes the same appsettings.json the service reads, using the exact same
/// Microsoft.Extensions.Configuration binding path so the Settings screen's idea of the
/// current values can never drift from what the service actually parses. If the file doesn't
/// exist yet, every section simply keeps its C# class defaults (same defaults the service
/// itself falls back to) — there is nothing special to "load" in that case.
/// </summary>
public static class ConfigStore
{
    public static ConfigBundle Load(string path)
    {
        var bundle = new ConfigBundle();

        if (!File.Exists(path))
        {
            return bundle;
        }

        // AddJsonFile(path) alone resolves relative paths ambiguously (against whatever base
        // path the builder happens to have, not necessarily the caller's working directory) —
        // pin base path and file name explicitly so this behaves the same regardless of
        // whether "path" arrived relative or absolute.
        var fullPath = Path.GetFullPath(path);
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Path.GetDirectoryName(fullPath)!)
            .AddJsonFile(Path.GetFileName(fullPath), optional: true, reloadOnChange: false)
            .Build();

        configuration.GetSection(GeneralOptions.SectionName).Bind(bundle.General);
        configuration.GetSection(MonitoringOptions.SectionName).Bind(bundle.Monitoring);
        configuration.GetSection(SmtpOptions.SectionName).Bind(bundle.Smtp);
        configuration.GetSection(DiscordOptions.SectionName).Bind(bundle.Discord);
        configuration.GetSection(DatabaseOptions.SectionName).Bind(bundle.Database);
        configuration.GetSection(FileLoggingOptions.SectionName).Bind(bundle.FileLogging);

        return bundle;
    }

    public static void Save(string path, ConfigBundle bundle)
    {
        var root = new Dictionary<string, object>
        {
            ["General"] = bundle.General,
            ["Monitoring"] = bundle.Monitoring,
            ["Smtp"] = bundle.Smtp,
            ["Discord"] = bundle.Discord,
            ["Database"] = bundle.Database,
            ["FileLogging"] = bundle.FileLogging,
            ["Logging"] = new { LogLevel = new { Default = "Information", Microsoft = "Warning" } },
        };

        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, json);
    }
}
