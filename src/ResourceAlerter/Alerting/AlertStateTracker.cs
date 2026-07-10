using Microsoft.Extensions.Logging;
using ResourceAlerter.Configuration;
using ResourceAlerter.Monitors;

namespace ResourceAlerter.Alerting;

/// <summary>
/// Per-subject anti-spam state machine. One instance is shared across all subjects reported
/// by a given monitor; each subject (e.g. each disk drive, each PSU rail) gets its own
/// independent state entry keyed by <see cref="MonitorResult.Subject"/>.
///
/// Flow: Normal -&gt; PendingAlert (sustained window) -&gt; Active (initial mail, then reminders
/// every ReminderIntervalMinutes) -&gt; PendingRecovery (recovery window) -&gt; Normal (resolved mail).
/// A flicker back out of range while PendingRecovery snaps straight back to Active without
/// re-sending the initial mail.
/// </summary>
public sealed class AlertStateTracker
{
    private enum State
    {
        Normal,
        PendingAlert,
        Active,
        PendingRecovery,
    }

    private sealed class Entry
    {
        public State State = State.Normal;
        public DateTimeOffset? OutOfRangeSince;
        public DateTimeOffset? InRangeSince;
        public DateTimeOffset? EventStartedAt;
        public DateTimeOffset? LastReminderSentAt;
        public string? LastDisplayValue;
        public string? LastDisplayThreshold;

        public DateTimeOffset? UnavailableWarnedAt;
    }

    private readonly string _monitorName;
    private readonly string _machineName;
    private readonly MonitorOptionsBase _options;
    private readonly IAlertSender _alertSender;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public AlertStateTracker(string monitorName, string machineName, MonitorOptionsBase options, IAlertSender alertSender, ILogger logger)
    {
        _monitorName = monitorName;
        _machineName = machineName;
        _options = options;
        _alertSender = alertSender;
        _logger = logger;
    }

    public async Task ProcessAsync(MonitorResult result, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(result.Subject, out entry!))
            {
                entry = new Entry();
                _entries[result.Subject] = entry;
            }
        }

        if (result.Unavailable)
        {
            await HandleUnavailableAsync(result, entry, cancellationToken);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        if (result.InRange)
        {
            await HandleInRangeAsync(result, entry, now, cancellationToken);
        }
        else
        {
            await HandleOutOfRangeAsync(result, entry, now, cancellationToken);
        }
    }

    /// <summary>
    /// A sensor that can't be read is silently ignored — no alert mail, ever. It's logged once
    /// per process lifetime for local diagnostics only (the startup mail is where the admin
    /// finds out what isn't being monitored; see <see cref="Worker"/>). Checking continues
    /// every cycle in case the sensor becomes available later (e.g. a driver comes up), which
    /// self-heals into the normal in-range/out-of-range flow without any special handling.
    /// </summary>
    private Task HandleUnavailableAsync(MonitorResult result, Entry entry, CancellationToken cancellationToken)
    {
        if (entry.UnavailableWarnedAt is not null)
        {
            return Task.CompletedTask;
        }

        entry.UnavailableWarnedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "{Monitor}/{Subject} sensor not available on this hardware ({Reason}); ignoring — no alert mail will be sent for it.",
            _monitorName, result.Subject, result.UnavailableReason);

        return Task.CompletedTask;
    }

    private async Task HandleInRangeAsync(MonitorResult result, Entry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        switch (entry.State)
        {
            case State.PendingAlert:
                // Blip: never actually sustained long enough to become a real event.
                entry.State = State.Normal;
                entry.OutOfRangeSince = null;
                break;

            case State.Active:
                entry.State = State.PendingRecovery;
                entry.InRangeSince = now;
                break;

            case State.PendingRecovery:
                if (entry.InRangeSince is not null &&
                    now - entry.InRangeSince.Value >= TimeSpan.FromSeconds(_options.RecoveryWindowSeconds))
                {
                    await SendResolvedAsync(result, entry, now, cancellationToken);
                    entry.State = State.Normal;
                    entry.EventStartedAt = null;
                    entry.InRangeSince = null;
                    entry.LastReminderSentAt = null;
                }
                break;

            case State.Normal:
            default:
                break;
        }
    }

    private async Task HandleOutOfRangeAsync(MonitorResult result, Entry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        entry.LastDisplayValue = result.DisplayValue;
        entry.LastDisplayThreshold = result.DisplayThreshold;

        switch (entry.State)
        {
            case State.Normal:
                entry.State = State.PendingAlert;
                entry.OutOfRangeSince = now;
                break;

            case State.PendingAlert:
                if (entry.OutOfRangeSince is not null &&
                    now - entry.OutOfRangeSince.Value >= TimeSpan.FromSeconds(_options.SustainedWindowSeconds))
                {
                    entry.State = State.Active;
                    entry.EventStartedAt = now;
                    entry.LastReminderSentAt = now;
                    await SendTriggeredAsync(result, entry, now, cancellationToken);
                }
                break;

            case State.PendingRecovery:
                // Flickered back out of range before recovery was confirmed; still an active event.
                entry.State = State.Active;
                entry.InRangeSince = null;
                break;

            case State.Active:
                if (entry.LastReminderSentAt is not null &&
                    now - entry.LastReminderSentAt.Value >= TimeSpan.FromMinutes(_options.ReminderIntervalMinutes))
                {
                    entry.LastReminderSentAt = now;
                    await SendReminderAsync(result, entry, now, cancellationToken);
                }
                break;
        }
    }

    private Task SendTriggeredAsync(MonitorResult result, Entry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _logger.LogWarning("{Monitor}/{Subject} ALERT: {Value} (threshold {Threshold})",
            _monitorName, result.Subject, result.DisplayValue, result.DisplayThreshold);

        return _alertSender.SendAsync(new AlertMessage
        {
            Kind = AlertKind.Triggered,
            Subject = $"[{_machineName}] ALERT: {_monitorName} - {result.Subject}",
            Body =
                $"Machine: {_machineName}\r\n" +
                $"Monitor: {_monitorName}\r\n" +
                $"Subject: {result.Subject}\r\n" +
                $"Detected value: {result.DisplayValue}\r\n" +
                $"Threshold: {result.DisplayThreshold}\r\n" +
                $"Event started: {entry.EventStartedAt:yyyy-MM-dd HH:mm:ss} UTC\r\n",
        }, cancellationToken);
    }

    private Task SendReminderAsync(MonitorResult result, Entry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var duration = entry.EventStartedAt is null ? TimeSpan.Zero : now - entry.EventStartedAt.Value;

        return _alertSender.SendAsync(new AlertMessage
        {
            Kind = AlertKind.Reminder,
            Subject = $"[{_machineName}] STILL ACTIVE: {_monitorName} - {result.Subject}",
            Body =
                $"Machine: {_machineName}\r\n" +
                $"Monitor: {_monitorName}\r\n" +
                $"Subject: {result.Subject}\r\n" +
                $"Current value: {result.DisplayValue}\r\n" +
                $"Threshold: {result.DisplayThreshold}\r\n" +
                $"Event started: {entry.EventStartedAt:yyyy-MM-dd HH:mm:ss} UTC\r\n" +
                $"Ongoing for: {FormatDuration(duration)}\r\n",
        }, cancellationToken);
    }

    private Task SendResolvedAsync(MonitorResult result, Entry entry, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var duration = entry.EventStartedAt is null ? TimeSpan.Zero : now - entry.EventStartedAt.Value;

        _logger.LogInformation("{Monitor}/{Subject} RESOLVED after {Duration}", _monitorName, result.Subject, duration);

        return _alertSender.SendAsync(new AlertMessage
        {
            Kind = AlertKind.Resolved,
            Subject = $"[{_machineName}] RESOLVED: {_monitorName} - {result.Subject}",
            Body =
                $"Machine: {_machineName}\r\n" +
                $"Monitor: {_monitorName}\r\n" +
                $"Subject: {result.Subject}\r\n" +
                $"Last detected value: {entry.LastDisplayValue}\r\n" +
                $"Threshold: {entry.LastDisplayThreshold}\r\n" +
                $"Event started: {entry.EventStartedAt:yyyy-MM-dd HH:mm:ss} UTC\r\n" +
                $"Resolved at: {now:yyyy-MM-dd HH:mm:ss} UTC\r\n" +
                $"Total duration: {FormatDuration(duration)}\r\n",
        }, cancellationToken);
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s";
        }
        if (span.TotalMinutes >= 1)
        {
            return $"{span.Minutes}m {span.Seconds}s";
        }
        return $"{span.Seconds}s";
    }
}
