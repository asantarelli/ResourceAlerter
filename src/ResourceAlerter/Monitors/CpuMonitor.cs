using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;

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
            _logger.LogError(ex, "Could not initialize CPU performance counter");
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
                    Subject = "Total",
                    InRange = true,
                    DisplayValue = "n/a",
                    DisplayThreshold = $"{_options.AlertThresholdPercent}%",
                    Unavailable = true,
                    UnavailableReason = "Processor performance counter could not be initialized.",
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
            _logger.LogWarning(ex, "Failed to read CPU counter");
            return new[]
            {
                new MonitorResult
                {
                    Subject = "Total",
                    InRange = true,
                    DisplayValue = "n/a",
                    DisplayThreshold = $"{_options.AlertThresholdPercent}%",
                    Unavailable = true,
                    UnavailableReason = "Failed to read the processor performance counter.",
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
                Subject = "Total",
                InRange = inRange,
                DisplayValue = $"{value:F1}%",
                DisplayThreshold = $"{_options.AlertThresholdPercent}% (recovery below {_options.RecoveryThresholdPercent}%)",
            },
        };
    }

    public void Dispose() => _counter?.Dispose();
}
