using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;

namespace ResourceAlerter.Monitors;

/// <summary>
/// Physical memory utilization, read directly via the Win32 GlobalMemoryStatusEx API
/// (total installed RAM vs. currently available RAM).
/// </summary>
public sealed class MemoryMonitor : IHealthMonitor
{
    public string Name => "Memory";
    public MonitorOptionsBase Options => _options;

    private readonly MemoryOptions _options;
    private readonly ILogger<MemoryMonitor> _logger;
    private bool _currentlyOutOfRange;

    public MemoryMonitor(IOptions<MonitoringOptions> options, ILogger<MemoryMonitor> logger)
    {
        _options = options.Value.Memory;
        _logger = logger;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public IReadOnlyList<MonitorResult> Check()
    {
        try
        {
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
            {
                throw new InvalidOperationException($"GlobalMemoryStatusEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
            }

            var total = status.ullTotalPhys;
            var available = status.ullAvailPhys;

            if (total == 0)
            {
                throw new InvalidOperationException("Reported total physical memory was 0.");
            }

            var usedPercent = 100.0 * (total - available) / total;

            var threshold = _currentlyOutOfRange ? _options.RecoveryThresholdPercent : _options.AlertThresholdPercent;
            _currentlyOutOfRange = usedPercent >= threshold;
            var inRange = !_currentlyOutOfRange;

            return new[]
            {
                new MonitorResult
                {
                    Subject = "Physical RAM",
                    InRange = inRange,
                    DisplayValue = $"{usedPercent:F1}% used ({FormatBytes(total - available)} / {FormatBytes(total)})",
                    NumericValue = usedPercent,
                    Unit = "% used",
                    DisplayThreshold = $"{_options.AlertThresholdPercent}% (recovery below {_options.RecoveryThresholdPercent}%)",
                },
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read memory status");
            return new[]
            {
                new MonitorResult
                {
                    Subject = "Physical RAM",
                    InRange = true,
                    DisplayValue = "n/a",
                    DisplayThreshold = $"{_options.AlertThresholdPercent}%",
                    Unavailable = true,
                    UnavailableReason = "Failed to read physical memory status.",
                },
            };
        }
    }

    private static string FormatBytes(ulong bytes)
    {
        const double gb = 1024d * 1024 * 1024;
        return $"{bytes / gb:F1} GB";
    }
}
