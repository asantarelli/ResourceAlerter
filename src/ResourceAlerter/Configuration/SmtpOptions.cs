namespace ResourceAlerter.Configuration;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

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
