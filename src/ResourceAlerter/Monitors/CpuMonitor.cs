using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Monitors;

/// <summary>
/// Total CPU utilization via the "Processor / % Processor Time / _Total" performance counter.
/// The sustained-window logic (avoiding alerts on a single spike) lives in
/// <see cref="Alerting.AlertStateTracker"/>, which only confirms an event once this monitor
/// has reported "out of range" on every poll for the configured window.
/// </summary>
public sealed class CpuMonitor : IHealthMonitor, IDisposable
{
    public string Name => "CPU";
    public MonitorOptionsBase Options => _options;

    private readonly CpuOptions _options;
    private readonly ILogger<CpuMonitor> _logger;
    private readonly PerformanceCounter? _counter;
    private bool _currentlyOutOfRange;

    public CpuMonitor(IOptions<MonitoringOptions> options, ILogger<CpuMonitor> logger)
    {
        _options = options.Value.Cpu;
        _logger = logger;

        try
        {
            _counter = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            // First read from a freshly created counter is always 0; prime it.
            _counter.NextValue();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, Strings.Log_CpuCounterInitFailed);
            _counter = null;
        }
    }

    public IReadOnlyList<MonitorResult> Check()
    {
        if (_counter is null)
        {
            return new[]
            {
                new MonitorResult
                {
                    Subject = Strings.Cpu_Subject,
                    InRange = true,
                    DisplayValue = Strings.NotAvailable,
                    DisplayThreshold = $"{Strings.FormatNumber(_options.AlertThresholdPercent)}%",
                    Unavailable = true,
                    UnavailableReason = Strings.Unavailable_ProcessorCounterInit,
                },
            };
        }

        float value;
        try
        {
            value = _counter.NextValue();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_CpuCounterReadFailed);
            return new[]
            {
                new MonitorResult
                {
                    Subject = Strings.Cpu_Subject,
                    InRange = true,
                    DisplayValue = Strings.NotAvailable,
                    DisplayThreshold = $"{Strings.FormatNumber(_options.AlertThresholdPercent)}%",
                    Unavailable = true,
                    UnavailableReason = Strings.Unavailable_ProcessorCounterRead,
                },
            };
        }

        // Hysteresis: once out of range, stay "out of range" until the value drops below the
        // (lower) recovery threshold, not merely back under the alert threshold.
        var threshold = _currentlyOutOfRange ? _options.RecoveryThresholdPercent : _options.AlertThresholdPercent;
        _currentlyOutOfRange = value >= threshold;
        var inRange = !_currentlyOutOfRange;

        return new[]
        {
            new MonitorResult
            {
                Subject = Strings.Cpu_Subject,
                InRange = inRange,
                DisplayValue = $"{Strings.FormatNumber(value, "F1")}%",
                NumericValue = value,
                Unit = "%",
                DisplayThreshold = $"{Strings.FormatNumber(_options.AlertThresholdPercent)}% {Strings.Monitor_RecoveryBelow(_options.RecoveryThresholdPercent)}",
            },
        };
    }

    public void Dispose() => _counter?.Dispose();
}
