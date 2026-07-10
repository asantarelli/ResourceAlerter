using ResourceAlerter.Configuration;

namespace ResourceAlerter.Monitors;

/// <summary>
/// A single health check. Implementations must be cheap to call repeatedly and must never
/// throw for expected failure conditions (missing sensor, transient counter glitch, etc.) —
/// report that via <see cref="MonitorResult.Unavailable"/> instead.
/// </summary>
public interface IHealthMonitor
{
    /// <summary>Stable identifier used for logging and as the alert-tracker key, e.g. "CPU", "Memory", "Disk:C:".</summary>
    string Name { get; }

    /// <summary>The monitor's own timing/threshold configuration, used to build its <see cref="Alerting.AlertStateTracker"/>.</summary>
    MonitorOptionsBase Options { get; }

    /// <summary>
    /// Takes one or more readings for this check. A monitor can report on several subjects at once
    /// (e.g. Disk reports one result per configured drive, Voltage one per rail).
    /// </summary>
    IReadOnlyList<MonitorResult> Check();
}
