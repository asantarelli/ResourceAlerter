using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Alerting;
using ResourceAlerter.Configuration;
using ResourceAlerter.Data;
using ResourceAlerter.Localization;
using ResourceAlerter.Monitors;
using ResourceAlerter.Reporting;

namespace ResourceAlerter;

/// <summary>
/// Single central polling loop. Every <see cref="MonitoringOptions.PollingIntervalSeconds"/>,
/// runs each enabled <see cref="IHealthMonitor"/> and feeds its results into that monitor's
/// dedicated <see cref="AlertStateTracker"/>, which owns the sustained/reminder/recovery
/// anti-spam logic and sends mail as needed.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly IEnumerable<IHealthMonitor> _monitors;
    private readonly MonitoringOptions _options;
    private readonly IAlertSender _alertSender;
    private readonly DataRecorder _dataRecorder;
    private readonly DailySummaryService _dailySummary;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<Worker> _logger;
    private readonly string _machineName;
    private readonly Dictionary<string, AlertStateTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _nextSummaryAt;

    public Worker(
        IEnumerable<IHealthMonitor> monitors,
        IOptions<MonitoringOptions> options,
        IOptions<GeneralOptions> generalOptions,
        IAlertSender alertSender,
        DataRecorder dataRecorder,
        DailySummaryService dailySummary,
        ILoggerFactory loggerFactory,
        ILogger<Worker> logger)
    {
        _monitors = monitors;
        _options = options.Value;
        _alertSender = alertSender;
        _dataRecorder = dataRecorder;
        _dailySummary = dailySummary;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _machineName = string.IsNullOrWhiteSpace(generalOptions.Value.MachineName)
            ? Environment.MachineName
            : generalOptions.Value.MachineName;
        _nextSummaryAt = DateTime.Today.AddDays(1); // next local midnight

        foreach (var monitor in _monitors)
        {
            _trackers[monitor.Name] = new AlertStateTracker(
                monitor.Name,
                _machineName,
                monitor.Options,
                _alertSender,
                _dataRecorder,
                _loggerFactory.CreateLogger($"ResourceAlerter.Alerting.{monitor.Name}"));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(Strings.Log_WorkerStarting,
            AppInfo.Version, _machineName, _options.PollingIntervalSeconds, string.Join(", ", _monitors.Select(m => m.Name)));

        // Probe once up front so the startup mail can report exactly what's actually being
        // watched on this machine (and what got skipped for lack of a sensor), then feed those
        // same results into the trackers as the first real cycle instead of checking twice.
        var initialCycle = ProbeAllMonitors();
        PruneOrphanedSeries(initialCycle);
        await SendStartupNotificationAsync(initialCycle, stoppingToken);
        await ProcessCycleAsync(initialCycle, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.PollingIntervalSeconds)));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ProcessCycleAsync(ProbeAllMonitors(), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private List<(IHealthMonitor Monitor, IReadOnlyList<MonitorResult> Results)> ProbeAllMonitors()
    {
        var cycle = new List<(IHealthMonitor, IReadOnlyList<MonitorResult>)>();

        foreach (var monitor in _monitors)
        {
            if (!monitor.Options.Enabled)
            {
                continue;
            }

            try
            {
                cycle.Add((monitor, monitor.Check()));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, Strings.Log_MonitorCheckThrew, monitor.Name);
            }
        }

        return cycle;
    }

    /// <summary>
    /// Discards recorded data for subjects the current configuration no longer produces (e.g. a
    /// disk drive removed from Disk.Drives) so the Viewer stops showing frozen, never-updating
    /// entries next to the real one. Only monitors present in <paramref name="cycle"/> (i.e.
    /// still enabled) are touched — see <see cref="DataRecorder.PruneOrphanedSeries"/>.
    ///
    /// A monitor is skipped entirely (left untouched this run) if any of its results this cycle
    /// came back Unavailable: an unavailable read can't reliably tell us the full subject set
    /// the config *should* produce — e.g. TemperatureMonitor's total-failure fallback reports a
    /// generic "CPU" subject instead of "CPU Package"/"CPU (avg of cores)", which would look
    /// like a config change and wrongly prune real history over a transient sensor hiccup right
    /// at startup. A real config change gets picked up on the next successful restart instead —
    /// a one-cycle delay beats risking data loss.
    /// </summary>
    private void PruneOrphanedSeries(List<(IHealthMonitor Monitor, IReadOnlyList<MonitorResult> Results)> cycle)
    {
        var activeSubjectsByMonitor = cycle
            .Where(c => c.Results.All(r => !r.Unavailable))
            .ToDictionary(
                c => c.Monitor.Name,
                c => c.Results.Select(r => r.Subject).ToHashSet(StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        _dataRecorder.PruneOrphanedSeries(activeSubjectsByMonitor);
    }

    private async Task ProcessCycleAsync(List<(IHealthMonitor Monitor, IReadOnlyList<MonitorResult> Results)> cycle, CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        var samples = new List<SampleRecord>();

        foreach (var (monitor, results) in cycle)
        {
            if (!_trackers.TryGetValue(monitor.Name, out var tracker))
            {
                continue;
            }

            foreach (var result in results)
            {
                if (!result.Unavailable && result.NumericValue is not null)
                {
                    samples.Add(new SampleRecord(monitor.Name, result.Subject, result.NumericValue.Value, result.Unit));
                }

                try
                {
                    await tracker.ProcessAsync(result, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, Strings.Log_AlertStateProcessingFailed, monitor.Name, result.Subject);
                }
            }
        }

        _dataRecorder.RecordSamples(samples, now);
        await SendDailySummaryIfDueAsync(stoppingToken);
    }

    private async Task SendDailySummaryIfDueAsync(CancellationToken stoppingToken)
    {
        if (DateTime.Now < _nextSummaryAt)
        {
            return;
        }

        _nextSummaryAt = DateTime.Today.AddDays(1);
        try
        {
            await _dailySummary.SendAsync(DateTimeOffset.Now, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, Strings.Log_DailySummaryFailed);
        }
    }

    private async Task SendStartupNotificationAsync(
        List<(IHealthMonitor Monitor, IReadOnlyList<MonitorResult> Results)> initialCycle,
        CancellationToken stoppingToken)
    {
        try
        {
            var recordingWarning = _dataRecorder.IsAvailable
                ? ""
                : Strings.Startup_RecordingWarning(_dataRecorder.InitializationError);

            await _alertSender.SendAsync(new AlertMessage
            {
                Kind = AlertKind.ServiceStarted,
                Subject = Strings.Subject_ServiceStarted(_machineName, warning: !_dataRecorder.IsAvailable),
                Body =
                    $"{Strings.Label_Machine}: {_machineName}\r\n" +
                    $"{Strings.Label_Version}: {AppInfo.Version}\r\n" +
                    $"{Strings.Label_StartedAt}: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\r\n" +
                    recordingWarning + "\r\n" +
                    BuildMonitoringSummary(initialCycle) +
                    "\r\n" + Strings.Startup_Footer,
            }, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_StartupNotificationFailed);
        }
    }

    private string BuildMonitoringSummary(List<(IHealthMonitor Monitor, IReadOnlyList<MonitorResult> Results)> cycle)
    {
        var monitored = new List<string>();
        var skipped = new List<string>();

        foreach (var (monitor, results) in cycle)
        {
            var monitorDisplay = Strings.MonitorDisplayName(monitor.Name);
            foreach (var result in results)
            {
                var label = $"{monitorDisplay} ({Strings.SubjectDisplayName(monitor.Name, result.Subject)})";
                if (result.Unavailable)
                {
                    skipped.Add($"  - {label}: {result.UnavailableReason}");
                }
                else
                {
                    monitored.Add($"  - {label}: {Strings.Word_Threshold} {result.DisplayThreshold}");
                }
            }
        }

        var disabled = _monitors
            .Where(m => !m.Options.Enabled)
            .Select(m => $"  - {Strings.MonitorDisplayName(m.Name)}: {Strings.Startup_DisabledInConfig}")
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine(Strings.Startup_ActivelyMonitoring);
        sb.AppendLine(monitored.Count > 0 ? string.Join("\r\n", monitored) : Strings.Startup_NothingCheckConfig);

        if (skipped.Count > 0 || disabled.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Strings.Startup_NotMonitoredHeader);
            foreach (var line in skipped)
            {
                sb.AppendLine(line);
            }
            foreach (var line in disabled)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString();
    }
}
