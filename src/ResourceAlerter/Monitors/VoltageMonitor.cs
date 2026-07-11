using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;

namespace ResourceAlerter.Monitors;

/// <summary>
/// PSU rail voltages (+12V, +5V, +3.3V, +5V standby) via LibreHardwareMonitor. Deliberately
/// ignores Vcore/VRM voltage sensors — those track CPU load, not PSU health.
/// Rail names reported by hardware vary a lot between motherboard Super I/O chips, so matching
/// is done by comparing the nominal voltage digits and standby-ness rather than exact string
/// equality; verify against this machine's actual sensor names if a rail never reports.
/// </summary>
public sealed class VoltageMonitor : IHealthMonitor
{
    public string Name => "Voltage";
    public MonitorOptionsBase Options => _options;

    private readonly VoltageOptions _options;
    private readonly HardwareMonitorAccessor _accessor;
    private readonly ILogger<VoltageMonitor> _logger;
    private readonly Dictionary<string, bool> _currentlyOutOfRange = new(StringComparer.OrdinalIgnoreCase);

    public VoltageMonitor(IOptions<MonitoringOptions> options, HardwareMonitorAccessor accessor, ILogger<VoltageMonitor> logger)
    {
        _options = options.Value.Voltage;
        _accessor = accessor;
        _logger = logger;
    }

    public IReadOnlyList<MonitorResult> Check()
    {
        if (!_accessor.IsAvailable)
        {
            return _options.NominalRails.Keys
                .Select(rail => Unavailable(rail, "LibreHardwareMonitor could not open (driver/privilege issue)."))
                .ToList();
        }

        var sensors = _accessor.GetSensors(SensorType.Voltage).ToList();
        var results = new List<MonitorResult>(_options.NominalRails.Count);

        foreach (var (railName, nominal) in _options.NominalRails)
        {
            var match = _options.SensorNameOverrides.TryGetValue(railName, out var overrideName)
                ? sensors.FirstOrDefault(s => string.Equals(s.Sensor.Name, overrideName, StringComparison.OrdinalIgnoreCase))
                : sensors.FirstOrDefault(s => IsMatchingRail(railName, s.Sensor.Name));

            if (match.Sensor is null)
            {
                results.Add(Unavailable(railName, "No matching voltage sensor found on this hardware."));
                continue;
            }

            var value = match.Sensor.Value!.Value;
            var deviation = Math.Abs(value - nominal) / nominal;

            var wasOut = _currentlyOutOfRange.TryGetValue(railName, out var flag) && flag;
            // Hysteresis: require dropping back to 80% of the allowed deviation to clear.
            var effectiveDeviationLimit = wasOut ? _options.AllowedDeviationFraction * 0.8 : _options.AllowedDeviationFraction;
            var isOut = deviation > effectiveDeviationLimit;
            _currentlyOutOfRange[railName] = isOut;

            results.Add(new MonitorResult
            {
                Subject = railName,
                InRange = !isOut,
                DisplayValue = $"{value:F2}V ({deviation:P1} off nominal)",
                NumericValue = value,
                Unit = "V",
                DisplayThreshold = $"{nominal}V ±{_options.AllowedDeviationFraction:P0}",
            });
        }

        return results;
    }

    /// <summary>Matches rails by nominal voltage digits and standby-ness rather than exact text.</summary>
    private static bool IsMatchingRail(string railName, string sensorName)
    {
        var railDigits = ExtractVoltageDigits(railName);
        var sensorDigits = ExtractVoltageDigits(sensorName);
        if (railDigits.Length == 0 || railDigits != sensorDigits)
        {
            return false;
        }

        return IsStandby(railName) == IsStandby(sensorName);
    }

    private static string ExtractVoltageDigits(string name)
    {
        var match = Regex.Match(name, @"\d+(\.\d+)?");
        return match.Success ? match.Value : string.Empty;
    }

    private static bool IsStandby(string name) =>
        name.Contains("standby", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("sb", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("vsb", StringComparison.OrdinalIgnoreCase);

    private static MonitorResult Unavailable(string railName, string reason) => new()
    {
        Subject = railName,
        InRange = true,
        DisplayValue = "n/a",
        DisplayThreshold = "n/a",
        Unavailable = true,
        UnavailableReason = reason,
    };
}
