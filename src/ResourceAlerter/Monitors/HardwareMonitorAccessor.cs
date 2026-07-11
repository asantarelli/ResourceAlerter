using LibreHardwareMonitor.Hardware;
using Microsoft.Extensions.Logging;

namespace ResourceAlerter.Monitors;

/// <summary>
/// Owns the single LibreHardwareMonitor <see cref="Computer"/> instance for the process
/// (opening its kernel driver is not free and must not be done once per monitor). Shared as a
/// singleton between <see cref="TemperatureMonitor"/> and <see cref="VoltageMonitor"/>.
/// Requires the process to run elevated (LocalSystem, when installed as a Windows service).
/// </summary>
public sealed class HardwareMonitorAccessor : IDisposable
{
    private readonly Computer _computer;
    private readonly ILogger<HardwareMonitorAccessor> _logger;
    private readonly bool _opened;

    public bool IsAvailable => _opened;

    public HardwareMonitorAccessor(ILogger<HardwareMonitorAccessor> logger)
    {
        _logger = logger;
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsMotherboardEnabled = true,
        };

        try
        {
            _computer.Open();
            _opened = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open LibreHardwareMonitor; temperature/voltage monitoring will be unavailable. " +
                                  "The service most likely needs to run as LocalSystem for the sensor driver to load.");
            _opened = false;
        }
    }

    /// <summary>
    /// Full diagnostic dump via LibreHardwareMonitor's own <see cref="Computer.GetReport"/> —
    /// the same report format used when filing hardware-support issues upstream. Includes the
    /// SMBIOS section (motherboard manufacturer/model, BIOS vendor/version) at the top, followed
    /// by the complete hardware/sensor tree with real chip names, not just sensor readings.
    /// Used in the daily summary mail so the admin can see exactly what model of hardware a
    /// machine has and plan which sensors to support next (e.g. via SensorNameOverrides).
    /// </summary>
    public string GetFullHardwareReport()
    {
        if (!_opened)
        {
            return "(LibreHardwareMonitor unavailable on this machine — no report to generate)";
        }

        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                UpdateRecursive(hardware);
            }
            return _computer.GetReport();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate the hardware report for the daily summary");
            return $"(hardware report generation failed: {ex.Message})";
        }
    }

    public IReadOnlyList<(IHardware Hardware, ISensor Sensor)> GetSensors(SensorType type)
    {
        if (!_opened)
        {
            return Array.Empty<(IHardware, ISensor)>();
        }

        var results = new List<(IHardware, ISensor)>();
        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                UpdateRecursive(hardware);
                CollectRecursive(hardware, type, results);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate {SensorType} sensors", type);
        }

        return results;
    }

    private static void UpdateRecursive(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware)
        {
            UpdateRecursive(sub);
        }
    }

    private static void CollectRecursive(IHardware hardware, SensorType type, List<(IHardware, ISensor)> results)
    {
        foreach (var sensor in hardware.Sensors)
        {
            if (sensor.SensorType == type && sensor.Value.HasValue)
            {
                results.Add((hardware, sensor));
            }
        }

        foreach (var sub in hardware.SubHardware)
        {
            CollectRecursive(sub, type, results);
        }
    }

    public void Dispose()
    {
        if (_opened)
        {
            try
            {
                _computer.Close();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error closing LibreHardwareMonitor computer instance");
            }
        }
    }
}
