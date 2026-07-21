namespace ResourceAlerter.Configuration;

public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    public int PollingIntervalSeconds { get; set; } = 10;

    public CpuOptions Cpu { get; set; } = new();
    public MemoryOptions Memory { get; set; } = new();
    public TemperatureOptions Temperature { get; set; } = new();
    public VoltageOptions Voltage { get; set; } = new();
    public NetworkOptions Network { get; set; } = new();
    public DiskOptions Disk { get; set; } = new();
}

public abstract class MonitorOptionsBase
{
    public bool Enabled { get; set; } = true;

    /// <summary>How long a value must stay out of range before an alert fires, in seconds.</summary>
    public int SustainedWindowSeconds { get; set; } = 60;

    /// <summary>How long a value must stay back in range before a "resolved" mail fires, in seconds.</summary>
    public int RecoveryWindowSeconds { get; set; } = 60;

    /// <summary>Minutes between reminder mails while an event is still active.</summary>
    public int ReminderIntervalMinutes { get; set; } = 20;
}

public sealed class CpuOptions : MonitorOptionsBase
{
    public double AlertThresholdPercent { get; set; } = 80;
    public double RecoveryThresholdPercent { get; set; } = 60;
}

public sealed class MemoryOptions : MonitorOptionsBase
{
    public double AlertThresholdPercent { get; set; } = 90;
    public double RecoveryThresholdPercent { get; set; } = 80;
}

public sealed class TemperatureOptions : MonitorOptionsBase
{
    public double AlertThresholdCelsius { get; set; } = 80;
}

public sealed class VoltageOptions : MonitorOptionsBase
{
    /// <summary>Allowed deviation from nominal, as a fraction (0.05 = 5%).</summary>
    public double AllowedDeviationFraction { get; set; } = 0.05;

    /// <summary>
    /// Optional per-rail override pinning the exact LibreHardwareMonitor sensor name to use
    /// (case-insensitive exact match, e.g. "AVCC3"). Use this when the automatic digit-based
    /// matcher can't find a rail because the Super I/O chip uses a non-obvious name — run
    /// `ResourceAlerter.exe --list-sensors` (elevated) to see the real names on a given
    /// machine. Set per-machine via appsettings.&lt;MACHINE-NAME&gt;.json; keys must match
    /// <see cref="NominalRails"/> keys.
    /// </summary>
    public Dictionary<string, string> SensorNameOverrides { get; set; } = new();

    /// <summary>Nominal PSU rail voltages to watch. Keys are label, values are nominal volts.</summary>
    public Dictionary<string, double> NominalRails { get; set; } = new()
    {
        ["+12V"] = 12.0,
        ["+5V"] = 5.0,
        ["+3.3V"] = 3.3,
        ["+5V Standby"] = 5.0,
    };
}

public sealed class NetworkOptions : MonitorOptionsBase
{
    /// <summary>Host to ping. Empty/null = auto-detect default gateway, falling back to a public DNS host.</summary>
    public string? TargetHost { get; set; }

    public string FallbackHost { get; set; } = "8.8.8.8";

    public int PingIntervalSeconds { get; set; } = 5;
    public int PingTimeoutMilliseconds { get; set; } = 2000;

    /// <summary>Size of the moving window of recent ping attempts.</summary>
    public int WindowSize { get; set; } = 10;

    /// <summary>Alert if this many losses occur within the window.</summary>
    public int MaxLossesInWindow { get; set; } = 3;

    /// <summary>Alert if this many consecutive seconds pass with no successful reply.</summary>
    public int MaxConsecutiveOutageSeconds { get; set; } = 30;

    /// <summary>Alert if a ping's round-trip time exceeds this many milliseconds.</summary>
    public int LatencyThresholdMilliseconds { get; set; } = 200;

    /// <summary>
    /// Name of the network interface to read error/discard and packet counters from (e.g.
    /// "Ethernet" — matches <c>NetworkInterface.Name</c>, not the longer hardware description).
    /// Run <c>ResourceAlerter.exe --list-network-interfaces</c> (elevated not required) to see
    /// exact names on a given machine. Null/empty or not found = those two subjects are silently
    /// skipped (same "unavailable sensor" pattern as a missing voltage rail) — ping-based
    /// losses/latency keep working regardless, since they don't depend on a specific local NIC.
    /// </summary>
    public string? InterfaceName { get; set; } = "Ethernet";

    /// <summary>Alert if interface errors+discards (sent+received) in one poll exceed this.</summary>
    public int MaxInterfaceErrorsPerInterval { get; set; } = 5;
}

public sealed class DiskOptions : MonitorOptionsBase
{
    public double FreeSpacePercentThreshold { get; set; } = 10;
    public double FreeSpaceAbsoluteGbThreshold { get; set; } = 5;

    /// <summary>Drive letters to check, e.g. ["C:", "D:"]. Empty = only the system drive.</summary>
    public List<string> Drives { get; set; } = new();
}
