using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;

namespace ResourceAlerter.Monitors;

/// <summary>
/// Free disk space on whichever drives are configured — <see cref="DiskOptions.Drives"/>, if
/// set, REPLACES the default (the system drive isn't watched unless it's explicitly listed).
/// This lets a server whose C: is roomy but whose D: holds the actual temp/swap/DB data watch
/// only the drive that matters. Alerts if free space drops below the configured percentage OR
/// the configured absolute GB floor, whichever is more restrictive (either condition alone triggers).
/// </summary>
public sealed class DiskMonitor : IHealthMonitor
{
    public string Name => "Disk";
    public MonitorOptionsBase Options => _options;

    private readonly DiskOptions _options;
    private readonly ILogger<DiskMonitor> _logger;

    public DiskMonitor(IOptions<MonitoringOptions> options, ILogger<DiskMonitor> logger)
    {
        _options = options.Value.Disk;
        _logger = logger;
    }

    public IReadOnlyList<MonitorResult> Check()
    {
        var drives = GetDrivesToCheck();
        var results = new List<MonitorResult>(drives.Count);

        foreach (var driveLetter in drives)
        {
            results.Add(CheckDrive(driveLetter));
        }

        return results;
    }

    private List<string> GetDrivesToCheck()
    {
        if (_options.Drives.Count > 0)
        {
            return _options.Drives;
        }

        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return new List<string> { systemDrive.TrimEnd('\\') };
    }

    private MonitorResult CheckDrive(string driveLetter)
    {
        var normalized = driveLetter.EndsWith(':') ? driveLetter + "\\" : driveLetter;

        try
        {
            var drive = new DriveInfo(normalized);
            if (!drive.IsReady)
            {
                return new MonitorResult
                {
                    Subject = driveLetter,
                    InRange = true,
                    DisplayValue = "n/a",
                    DisplayThreshold = $"{_options.FreeSpacePercentThreshold}% / {_options.FreeSpaceAbsoluteGbThreshold} GB",
                    Unavailable = true,
                    UnavailableReason = "Drive is not ready.",
                };
            }

            var freeBytes = drive.AvailableFreeSpace;
            var totalBytes = drive.TotalSize;
            var freePercent = totalBytes == 0 ? 100.0 : 100.0 * freeBytes / totalBytes;
            var freeGb = freeBytes / (1024d * 1024 * 1024);

            var belowPercent = freePercent < _options.FreeSpacePercentThreshold;
            var belowAbsolute = freeGb < _options.FreeSpaceAbsoluteGbThreshold;
            var inRange = !(belowPercent || belowAbsolute);

            return new MonitorResult
            {
                Subject = driveLetter,
                InRange = inRange,
                DisplayValue = $"{freeGb:F1} GB free ({freePercent:F1}%)",
                NumericValue = freeGb,
                Unit = "GB free",
                DisplayThreshold = $"below {_options.FreeSpacePercentThreshold}% or {_options.FreeSpaceAbsoluteGbThreshold} GB",
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read disk usage for {Drive}", driveLetter);
            return new MonitorResult
            {
                Subject = driveLetter,
                InRange = true,
                DisplayValue = "n/a",
                DisplayThreshold = $"{_options.FreeSpacePercentThreshold}% / {_options.FreeSpaceAbsoluteGbThreshold} GB",
                Unavailable = true,
                UnavailableReason = $"Failed to read drive: {ex.Message}",
            };
        }
    }
}
