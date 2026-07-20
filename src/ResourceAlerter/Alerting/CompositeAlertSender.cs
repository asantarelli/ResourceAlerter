using Microsoft.Extensions.Logging;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Alerting;

/// <summary>
/// Fans an alert out to every configured channel. Mail is the source-of-truth channel (it
/// carries attachments and is what every existing "did it send?" signal — the Viewer's
/// send-summary button, --send-summary's exit code — is based on), so this reports the mail
/// result. Discord is a parallel, best-effort notification: attempted regardless, its own
/// success/failure is logged but never affects the overall result or blocks mail delivery.
/// </summary>
public sealed class CompositeAlertSender : IAlertSender
{
    private readonly SmtpAlertSender _mailSender;
    private readonly DiscordAlertSender _discordSender;
    private readonly ILogger<CompositeAlertSender> _logger;

    public CompositeAlertSender(SmtpAlertSender mailSender, DiscordAlertSender discordSender, ILogger<CompositeAlertSender> logger)
    {
        _mailSender = mailSender;
        _discordSender = discordSender;
        _logger = logger;
    }

    public async Task<bool> SendAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        var mailSent = await _mailSender.SendAsync(message, cancellationToken);

        if (_discordSender.IsConfigured)
        {
            try
            {
                await _discordSender.SendAsync(message, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, Strings.Log_DiscordUnexpectedFail, message.Subject);
            }
        }

        return mailSent;
    }
}
