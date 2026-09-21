namespace ResourceAlerter.Configuration;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>
    /// False turns SMTP notifications off entirely (no send attempts, no "no recipients"
    /// warnings) — e.g. when Discord is the only channel wanted. Defaults to true so an existing
    /// appsettings.json that predates this setting keeps sending exactly as before.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 25;
    public bool UseSsl { get; set; } = false;
    public bool RequiresAuthentication { get; set; } = false;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromDisplayName { get; set; } = "ResourceAlerter";
    public List<string> Recipients { get; set; } = new();

    public int RetryCount { get; set; } = 3;
    public int RetryBackoffSeconds { get; set; } = 5;
    public int TimeoutMilliseconds { get; set; } = 15000;
}
