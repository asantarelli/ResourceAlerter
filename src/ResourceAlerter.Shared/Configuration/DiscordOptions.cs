namespace ResourceAlerter.Configuration;

public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Incoming webhook URL for the target channel (Channel Settings → Integrations → Webhooks
    /// in Discord — no bot or API key needed). Alerts are sent as embeds in parallel with mail;
    /// unlike mail, no attachments (charts/logs/hardware report) are sent to Discord.
    /// </summary>
    public string WebhookUrl { get; set; } = string.Empty;
}
