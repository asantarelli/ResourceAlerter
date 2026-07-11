using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Alerting;
using ResourceAlerter.Configuration;
using ResourceAlerter.Data;
using ResourceAlerter.Monitors;

namespace ResourceAlerter.Reporting;

/// <summary>
/// Builds and sends the 00:00 daily summary mail: the last 24 hours' alert list, one JPG
/// chart per recorded variable (red vertical lines at alert starts), the day's log file(s),
/// and a dump of every hardware sensor this machine exposes (so the admin can plan which
/// sensors to support in future service updates).
/// </summary>
public sealed class DailySummaryService
{
    private readonly DataRecorder _recorder;
    private readonly HardwareMonitorAccessor _hardware;
    private readonly IAlertSender _alertSender;
    private readonly FileLoggingOptions _loggingOptions;
    private readonly string _machineName;
    private readonly ILogger<DailySummaryService> _logger;

    public DailySummaryService(
        DataRecorder recorder,
        HardwareMonitorAccessor hardware,
        IAlertSender alertSender,
        IOptions<GeneralOptions> generalOptions,
        IConfiguration configuration,
        ILogger<DailySummaryService> logger)
    {
        _recorder = recorder;
        _hardware = hardware;
        _alertSender = alertSender;
        _logger = logger;
        _machineName = string.IsNullOrWhiteSpace(generalOptions.Value.MachineName)
            ? Environment.MachineName
            : generalOptions.Value.MachineName!;

        _loggingOptions = new FileLoggingOptions();
        configuration.GetSection(FileLoggingOptions.SectionName).Bind(_loggingOptions);
    }

    public async Task SendAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var to = now;
        var from = now.AddHours(-24);
        var summaryDate = now.LocalDateTime.AddSeconds(-1).Date; // the day that just ended

        var alerts = _recorder.GetAlertEvents(from, to);
        var attachments = new List<MailAttachment>();

        AddChartAttachments(attachments, from, to, alerts);
        AddLogAttachments(attachments, summaryDate);

        var body = BuildBody(summaryDate, from, to, alerts);

        await _alertSender.SendAsync(new AlertMessage
        {
            Kind = AlertKind.DailySummary,
            Subject = $"[{_machineName}] Daily summary {summaryDate:yyyy-MM-dd} — {alerts.Count} alert(s)",
            Body = body,
            Attachments = attachments,
        }, cancellationToken);

        _logger.LogInformation("Daily summary for {Date} sent ({Alerts} alerts, {Attachments} attachments)",
            summaryDate, alerts.Count, attachments.Count);
    }

    private void AddChartAttachments(List<MailAttachment> attachments, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<AlertEventRecord> alerts)
    {
        foreach (var (monitor, subject, unit) in _recorder.GetSeries())
        {
            try
            {
                var samples = _recorder.GetSamples(monitor, subject, from, to);
                if (samples.Count == 0)
                {
                    continue;
                }

                var seriesAlerts = alerts
                    .Where(a => a.Monitor.Equals(monitor, StringComparison.OrdinalIgnoreCase) &&
                                a.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.StartedAt);

                var jpeg = ChartRenderer.RenderSeriesJpeg($"{monitor} — {subject} (last 24h)", unit, samples, seriesAlerts);
                var safeName = $"{monitor}_{subject}".Replace(':', '_').Replace('\\', '_').Replace('/', '_').Replace(' ', '_');
                attachments.Add(new MailAttachment($"{safeName}.jpg", jpeg, "image/jpeg"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to render chart for {Monitor}/{Subject}", monitor, subject);
            }
        }
    }

    private void AddLogAttachments(List<MailAttachment> attachments, DateTime summaryDate)
    {
        try
        {
            var directory = Path.IsPathRooted(_loggingOptions.Directory)
                ? _loggingOptions.Directory
                : Path.Combine(AppContext.BaseDirectory, _loggingOptions.Directory);

            // Includes size-rolled files (resourcealerter-yyyyMMdd_1.log etc.). FileShare-friendly
            // read because the provider keeps today's file open.
            foreach (var file in Directory.EnumerateFiles(directory, $"resourcealerter-{summaryDate:yyyyMMdd}*.log"))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                attachments.Add(new MailAttachment(Path.GetFileName(file), memory.ToArray(), "text/plain"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to attach the day's log file(s)");
        }
    }

    private string BuildBody(DateTime summaryDate, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<AlertEventRecord> alerts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Machine: {_machineName}");
        sb.AppendLine($"Daily summary for: {summaryDate:yyyy-MM-dd}");
        sb.AppendLine($"Period: {from.LocalDateTime:yyyy-MM-dd HH:mm} — {to.LocalDateTime:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        if (alerts.Count == 0)
        {
            sb.AppendLine("No alerts in the last 24 hours.");
        }
        else
        {
            sb.AppendLine($"Alerts in the last 24 hours ({alerts.Count}):");
            foreach (var alert in alerts)
            {
                var duration = alert.ResolvedAt is null
                    ? "STILL ACTIVE"
                    : FormatDuration(alert.ResolvedAt.Value - alert.StartedAt);
                sb.AppendLine($"  - {alert.StartedAt.LocalDateTime:HH:mm:ss} {alert.Monitor}/{alert.Subject}: " +
                              $"{alert.DetectedValue} (threshold {alert.Threshold}) — {duration}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Attached: one chart per monitored variable (red vertical lines mark alert starts) and the day's log file(s).");
        sb.AppendLine();
        sb.AppendLine("All hardware sensors exposed on this machine (for planning future monitoring support):");
        foreach (var line in _hardware.DescribeAllSensors())
        {
            sb.AppendLine("  " + line);
        }

        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s"
        : $"{span.Seconds}s";
}
