using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;

namespace ResourceAlerter.Alerting;

/// <summary>
/// Posts alerts to a Discord incoming webhook (Channel Settings → Integrations → Webhooks —
/// no bot or API token needed) as an embed. Runs alongside mail, not instead of it: unlike
/// mail, no attachments (charts/logs/hardware report) are sent here, since a plain webhook post
/// doesn't carry files — Discord is the "quick heads up", mail stays the channel with full detail.
/// </summary>
public sealed class DiscordAlertSender
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordAlertSender> _logger;

    public DiscordAlertSender(IOptions<DiscordOptions> options, ILogger<DiscordAlertSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.Enabled && !string.IsNullOrWhiteSpace(_options.WebhookUrl);

    public async Task<bool> SendAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            var color = message.Kind switch
            {
                AlertKind.Triggered => 0xE74C3C,        // red
                AlertKind.Resolved => 0x2ECC71,         // green
                AlertKind.Reminder => 0xF39C12,         // amber
                AlertKind.SensorUnavailable => 0x95A5A6, // gray
                _ => 0x3498DB,                          // blue (started / daily summary)
            };

            // Discord embed description is capped at 4096 chars; truncate generously and point
            // back at the fuller mail rather than risk the whole post being rejected.
            const int maxDescriptionLength = 3800;
            var description = message.Body.Length > maxDescriptionLength
                ? message.Body[..maxDescriptionLength] + "\r\n… (truncated — see the e-mail for full detail)"
                : message.Body;

            var payload = new
            {
                embeds = new[]
                {
                    new
                    {
                        title = Truncate(message.Subject, 256),
                        description,
                        color,
                    },
                },
            };

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await HttpClient.PostAsync(_options.WebhookUrl, content, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Discord alert sent: {Subject}", message.Subject);
                return true;
            }

            _logger.LogWarning("Discord webhook returned {StatusCode} for '{Subject}'", response.StatusCode, message.Subject);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send Discord alert '{Subject}'", message.Subject);
            return false;
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length > maxLength ? value[..maxLength] : value;
}
