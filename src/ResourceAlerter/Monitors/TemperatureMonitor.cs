using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Monitors;

/// <summary>
/// CPU temperature via LibreHardwareMonitor. Prefers a "CPU Package" sensor if the chip
/// exposes one; otherwise averages whatever per-core CPU temperature sensors are available.
/// </summary>
public sealed class TemperatureMonitor : IHealthMonitor
{
    public string Name => "Temperature";
    public MonitorOptionsBase Options => _options;

    private readonly TemperatureOptions _options;
    private readonly HardwareMonitorAccessor _accessor;
    private readonly ILogger<TemperatureMonitor> _logger;
    private bool _currentlyOutOfRange;

    public TemperatureMonitor(IOptions<MonitoringOptions> options, HardwareMonitorAccessor accessor, ILogger<TemperatureMonitor> logger)
    {
        _options = options.Value.Temperature;
        _accessor = accessor;
        _logger = logger;
    }

    public IReadOnlyList<MonitorResult> Check()
    {
        if (!_accessor.IsAvailable)
        {
            return Unavailable(Strings.Unavailable_HardwareMonitorClosed);
        }

        var sensors = _accessor.GetSensors(SensorType.Temperature);
        var cpuSensors = sensors.Where(s => s.Hardware.HardwareType == HardwareType.Cpu).ToList();

        if (cpuSensors.Count == 0)
        {
            return Unavailable(Strings.Unavailable_NoCpuTempSensors);
        }

        var package = cpuSensors.FirstOrDefault(s => s.Sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase));

        float value;
        string subject;
        if (package.Sensor is not null)
        {
            value = package.Sensor.Value!.Value;
            subject = Strings.Temperature_CpuPackageKey;
        }
        else
        {
            value = cpuSensors.Average(s => s.Sensor.Value!.Value);
            subject = Strings.Temperature_CpuAvgOfCoresKey;
        }

        var threshold = _options.AlertThresholdCelsius;
        // Small hysteresis band (5% of threshold) to avoid flicker right at the line.
        var recoveryThreshold = threshold - (threshold * 0.05);
        var effectiveThreshold = _currentlyOutOfRange ? recoveryThreshold : threshold;
        _currentlyOutOfRange = value >= effectiveThreshold;

        return new[]
        {
            new MonitorResult
            {
                Subject = subject,
                InRange = !_currentlyOutOfRange,
                DisplayValue = $"{Strings.FormatNumber(value, "F1")}°C",
                NumericValue = value,
                Unit = "°C",
                DisplayThreshold = $"{Strings.FormatNumber(threshold, "F0")}°C",
            },
        };
    }

    private static IReadOnlyList<MonitorResult> Unavailable(string reason) => new[]
    {
        new MonitorResult
        {
            Subject = "CPU",
            InRange = true,
            DisplayValue = Strings.NotAvailable,
            DisplayThreshold = Strings.NotAvailable,
            Unavailable = true,
            UnavailableReason = reason,
        },
    };
}
