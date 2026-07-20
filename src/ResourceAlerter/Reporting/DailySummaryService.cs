using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Alerting;
using ResourceAlerter.Configuration;
using ResourceAlerter.Data;
using ResourceAlerter.Localization;
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

    /// <returns>True if the mail was actually sent successfully.</returns>
    public async Task<bool> SendAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var to = now;
        var from = now.AddHours(-24);
        var summaryDate = now.LocalDateTime.AddSeconds(-1).Date; // the day that just ended

        var alerts = _recorder.GetAlertEvents(from, to);
        var attachments = new List<MailAttachment>();

        AddChartAttachments(attachments, from, to, alerts);
        AddLogAttachments(attachments, summaryDate);
        AddHardwareReportAttachment(attachments);

        var body = BuildBody(summaryDate, from, to, alerts);

        var sent = await _alertSender.SendAsync(new AlertMessage
        {
            Kind = AlertKind.DailySummary,
            Subject = Strings.Subject_DailySummary(_machineName, summaryDate, alerts.Count),
            Body = body,
            Attachments = attachments,
        }, cancellationToken);

        _logger.LogInformation(Strings.Log_DailySummaryResult,
            summaryDate, sent ? Strings.Log_Sent : Strings.Log_FailedToSend, alerts.Count, attachments.Count);

        return sent;
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

                var chartTitle = $"{Strings.MonitorDisplayName(monitor)} — {Strings.SubjectDisplayName(monitor, subject)} " +
                                  Strings.T("(últimas 24h)", "(last 24h)");
                var jpeg = ChartRenderer.RenderSeriesJpeg(chartTitle, unit, samples, seriesAlerts);
                var safeName = $"{monitor}_{subject}".Replace(':', '_').Replace('\\', '_').Replace('/', '_').Replace(' ', '_');
                attachments.Add(new MailAttachment($"{safeName}.jpg", jpeg, "image/jpeg"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, Strings.Log_ChartRenderFailed, monitor, subject);
            }
        }
    }

    private void AddHardwareReportAttachment(List<MailAttachment> attachments)
    {
        try
        {
            var report = _hardware.GetFullHardwareReport();
            attachments.Add(new MailAttachment("hardware-report.txt", System.Text.Encoding.UTF8.GetBytes(report), "text/plain"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_HwReportAttachFailed);
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
            _logger.LogWarning(ex, Strings.Log_LogAttachFailed);
        }
    }

    private string BuildBody(DateTime summaryDate, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<AlertEventRecord> alerts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Strings.Label_Machine}: {_machineName}");
        sb.AppendLine($"{Strings.Label_Version}: {AppInfo.Version}");
        sb.AppendLine($"{Strings.DailySummary_For}: {summaryDate:yyyy-MM-dd}");
        sb.AppendLine($"{Strings.DailySummary_Period}: {from.LocalDateTime:yyyy-MM-dd HH:mm} — {to.LocalDateTime:yyyy-MM-dd HH:mm}");
        sb.AppendLine();

        if (alerts.Count == 0)
        {
            sb.AppendLine(Strings.DailySummary_NoAlerts);
        }
        else
        {
            sb.AppendLine(Strings.DailySummary_AlertsHeader(alerts.Count));
            foreach (var alert in alerts)
            {
                var duration = alert.ResolvedAt is null
                    ? Strings.Word_StillActive
                    : FormatDuration(alert.ResolvedAt.Value - alert.StartedAt);
                var monitorDisplay = Strings.MonitorDisplayName(alert.Monitor);
                var subjectDisplay = Strings.SubjectDisplayName(alert.Monitor, alert.Subject);
                sb.AppendLine($"  - {alert.StartedAt.LocalDateTime:HH:mm:ss} {monitorDisplay}/{subjectDisplay}: " +
                              $"{alert.DetectedValue} ({Strings.Word_Threshold} {alert.Threshold}) — {duration}");
            }
        }

        sb.AppendLine();
        sb.AppendLine(Strings.DailySummary_AttachedFooter);

        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
        : span.TotalMinutes >= 1 ? $"{span.Minutes}m {span.Seconds}s"
        : $"{span.Seconds}s";
}
