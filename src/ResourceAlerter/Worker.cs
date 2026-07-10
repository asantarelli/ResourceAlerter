using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Alerting;
using ResourceAlerter.Configuration;
using ResourceAlerter.Monitors;

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
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<Worker> _logger;
    private readonly string _machineName;
    private readonly Dictionary<string, AlertStateTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);

    public Worker(
        IEnumerable<IHealthMonitor> monitors,
        IOptions<MonitoringOptions> options,
        IOptions<GeneralOptions> generalOptions,
        IAlertSender alertSender,
        ILoggerFactory loggerFactory,
        ILogger<Worker> logger)
    {
        _monitors = monitors;
        _options = options.Value;
        _alertSender = alertSender;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _machineName = string.IsNullOrWhiteSpace(generalOptions.Value.MachineName)
            ? Environment.MachineName
            : generalOptions.Value.MachineName;

        foreach (var monitor in _monitors)
        {
            _trackers[monitor.Name] = new AlertStateTracker(
                monitor.Name,
                _machineName,
                monitor.Options,
                _alertSender,
                _loggerFactory.CreateLogger($"ResourceAlerter.Alerting.{monitor.Name}"));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ResourceAlerter starting on {Machine}. Polling interval: {Interval}s. Monitors: {Monitors}",
            _machineName, _options.PollingIntervalSeconds, string.Join(", ", _monitors.Select(m => m.Name)));

        // Probe once up front so the startup mail can report exactly what's actually being
        // watched on this machine (and what got skipped for lack of a sensor), then feed those
        // same results into the trackers as the first real cycle instead of checking twice.
        var initialCycle = ProbeAllMonitors();
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
                _logger.LogError(ex, "Monitor {Monitor} threw during Check(); skipping this cycle", monitor.Name);
            }
        }

        return cycle;
    }

    private async Task ProcessCycleAsync(List<(IHealthMonitor Monitor, IReadOnlyList<MonitorResult> Results)> cycle, CancellationToken stoppingToken)
    {
        foreach (var (monitor, results) in cycle)
        {
            if (!_trackers.TryGetValue(monitor.Name, out var tracker))
            {
                continue;
            }

            foreach (var result in results)
            {
                try
                {
                    await tracker.ProcessAsync(result, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed processing alert state for {Monitor}/{Subject}", monitor.Name, result.Subject);
                }
            }
        }
    }

    private async Task SendStartupNotificationAsync(
        List<(IHealthMonitor Monitor, IReadOnlyList<MonitorResult> Results)> initialCycle,
        CancellationToken stoppingToken)
    {
        try
        {
            await _alertSender.SendAsync(new AlertMessage
            {
                Kind = AlertKind.ServiceStarted,
                Subject = $"[{_machineName}] ResourceAlerter service started",
                Body =
                    $"Machine: {_machineName}\r\n" +
                    $"Started at: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}\r\n\r\n" +
                    BuildMonitoringSummary(initialCycle) +
                    "\r\nThis is an informational message sent whenever the service starts " +
                    "(including after a reboot) — no action required unless it was unexpected.",
            }, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send startup notification mail");
        }
    }

    private string BuildMonitoringSummary(List<(IHealthMonitor Monitor, IReadOnlyList<MonitorResult> Results)> cycle)
    {
        var monitored = new List<string>();
        var skipped = new List<string>();

        foreach (var (monitor, results) in cycle)
        {
            foreach (var result in results)
            {
                var label = $"{monitor.Name} ({result.Subject})";
                if (result.Unavailable)
                {
                    skipped.Add($"  - {label}: {result.UnavailableReason}");
                }
                else
                {
                    monitored.Add($"  - {label}: threshold {result.DisplayThreshold}");
                }
            }
        }

        var disabled = _monitors
            .Where(m => !m.Options.Enabled)
            .Select(m => $"  - {m.Name}: disabled in configuration")
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Actively monitoring:");
        sb.AppendLine(monitored.Count > 0 ? string.Join("\r\n", monitored) : "  (nothing — check configuration)");

        if (skipped.Count > 0 || disabled.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Not monitored on this machine (no alerts will be sent for these):");
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
