using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Alerting;

/// <summary>
/// Sends alert mail via SMTP with retry/backoff. A send that ultimately fails is logged
/// locally and swallowed — mail delivery problems must never bring the service down.
/// </summary>
public sealed class SmtpAlertSender : IAlertSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpAlertSender> _logger;

    public SmtpAlertSender(IOptions<SmtpOptions> options, ILogger<SmtpAlertSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        if (_options.Recipients.Count == 0)
        {
            _logger.LogWarning(Strings.Log_NoRecipients, message.Subject);
            return false;
        }

        for (var attempt = 1; attempt <= Math.Max(1, _options.RetryCount); attempt++)
        {
            try
            {
                await SendOnceAsync(message, cancellationToken);
                _logger.LogInformation(Strings.Log_MailSent, message.Subject);
                return true;
            }
            catch (Exception ex) when (attempt < _options.RetryCount)
            {
                var delay = TimeSpan.FromSeconds(_options.RetryBackoffSeconds * attempt);
                _logger.LogWarning(ex,
                    Strings.Log_MailRetrying,
                    attempt, _options.RetryCount, message.Subject, delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    Strings.Log_MailGivenUp,
                    attempt, message.Subject);
                return false;
            }
        }

        return false;
    }

    private async Task SendOnceAsync(AlertMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            Timeout = _options.TimeoutMilliseconds,
        };

        if (_options.RequiresAuthentication)
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }
        else
        {
            client.UseDefaultCredentials = false;
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromDisplayName),
            Subject = message.Subject,
            Body = message.Body,
            IsBodyHtml = false,
        };

        foreach (var recipient in _options.Recipients)
        {
            mail.To.Add(recipient);
        }

        foreach (var attachment in message.Attachments)
        {
            mail.Attachments.Add(new Attachment(new MemoryStream(attachment.Content), attachment.FileName, attachment.MimeType));
        }

        // SmtpClient.Timeout only applies to the synchronous Send(); for SendMailAsync the
        // linked token is the real timeout. Attachment-heavy mail (daily summary charts +
        // logs) takes much longer to stream/scan on some relays, so give it more headroom.
        var timeoutMs = message.Attachments.Count > 0
            ? Math.Max(_options.TimeoutMilliseconds, 120_000)
            : _options.TimeoutMilliseconds;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);
        await client.SendMailAsync(mail, cts.Token);
    }
}
