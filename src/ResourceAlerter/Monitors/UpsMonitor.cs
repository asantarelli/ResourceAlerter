using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;
using ResourceAlerter.Data;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Monitors;

/// <summary>
/// UPS monitoring through what Windows already knows: a UPS connected by USB is exposed as a
/// battery, so a power cut is simply "no longer on mains power", and the UPS charge is the battery
/// charge. Two subjects:
///  - "Power Source": alert while NOT on mains power (a power outage).
///  - "Battery Charge": alert while on battery AND at or below the configured level, so there is a
///    warning before Windows' own low/critical-battery shutdown.
/// With no battery at all (a normal desktop with no UPS) it reports a single Unavailable result,
/// which is what lets the startup notification say so without ever alerting.
/// Every reading is also recorded in <see cref="RuntimeStateStore"/> so that, after a reboot, the
/// previous shutdown can be explained ("it stopped while on battery at 23%").
/// </summary>
public sealed class UpsMonitor : IHealthMonitor
{
    public string Name => "Ups";
    public MonitorOptionsBase Options => _options;

    private readonly UpsOptions _options;
    private readonly IPowerStatusReader _reader;
    private readonly RuntimeStateStore _state;
    private readonly ILogger<UpsMonitor> _logger;

    public UpsMonitor(IOptions<MonitoringOptions> options, IPowerStatusReader reader, RuntimeStateStore state, ILogger<UpsMonitor> logger)
    {
        _options = options.Value.Ups;
        _reader = reader;
        _state = state;
        _logger = logger;
    }

    public IReadOnlyList<MonitorResult> Check()
    {
        PowerStatus status;
        try
        {
            status = _reader.Read();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_UpsReadFailed);
            _state.RecordUps(null);
            return new[] { Unavailable(Strings.Ups_PowerSubjectKey, Strings.Unavailable_UpsReadFailed(ex.Message)) };
        }

        if (!status.BatteryPresent)
        {
            _state.RecordUps(null);
            return new[] { Unavailable(Strings.Ups_PowerSubjectKey, Strings.Unavailable_UpsNotDetected) };
        }

        if (status.OnMains is null)
        {
            _state.RecordUps(null);
            return new[] { Unavailable(Strings.Ups_PowerSubjectKey, Strings.Unavailable_UpsSourceUnknown) };
        }

        var onBattery = status.OnMains == false;
        _state.RecordUps(new UpsSnapshot(onBattery, status.ChargePercent, status.RuntimeMinutes, DateTimeOffset.UtcNow));

        var power = new MonitorResult
        {
            Subject = Strings.Ups_PowerSubjectKey,
            InRange = !onBattery,
            DisplayValue = onBattery ? Strings.Ups_OnBatteryPower : Strings.Ups_OnMains,
            NumericValue = onBattery ? 0 : 1,
            Unit = "AC=1, battery=0",
            DisplayThreshold = Strings.Ups_PowerThreshold,
        };

        if (status.ChargePercent is not { } percent)
        {
            return new[] { power, Unavailable(Strings.Ups_BatterySubjectKey, Strings.Unavailable_UpsChargeUnknown) };
        }

        var battery = new MonitorResult
        {
            Subject = Strings.Ups_BatterySubjectKey,
            InRange = !(onBattery && percent <= _options.LowBatteryThresholdPercent),
            DisplayValue = Strings.Ups_BatteryValue(percent, onBattery, status.RuntimeMinutes),
            NumericValue = percent,
            Unit = "%",
            DisplayThreshold = Strings.Ups_BatteryThreshold(_options.LowBatteryThresholdPercent),
        };

        return new[] { power, battery };
    }

    private static MonitorResult Unavailable(string subject, string reason) => new()
    {
        Subject = subject,
        InRange = true,
        DisplayValue = Strings.NotAvailable,
        DisplayThreshold = Strings.NotAvailable,
        Unavailable = true,
        UnavailableReason = reason,
    };
}
