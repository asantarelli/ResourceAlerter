using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;
using ResourceAlerter.Data;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Reporting;

/// <summary>A System event-log entry reduced to what shutdown analysis needs. <see cref="Data"/> is positional (event 1074 has unnamed fields), <see cref="Named"/> is by field name (Kernel-Power 41/109).</summary>
public sealed record ShutdownEvent(DateTime TimeLocal, string Provider, int Id, IReadOnlyList<string> Data, IReadOnlyDictionary<string, string> Named);

public interface IShutdownEventSource
{
    /// <summary>Recent shutdown-related events, newest first.</summary>
    IReadOnlyList<ShutdownEvent> ReadRecent();
}

/// <summary>
/// Reads the Windows System log. Only IDs that describe how a session ended are requested:
/// 6005 (log service started = boot), 6006 (log service stopped = orderly shutdown), 6008
/// (previous shutdown was unexpected), 41 (Kernel-Power: rebooted without a clean shutdown),
/// 109 (Kernel-Power: shutdown initiated) and 1074 (who/why initiated a shutdown or restart).
/// </summary>
public sealed class WindowsShutdownEventSource : IShutdownEventSource
{
    private const int MaxEvents = 300;

    public IReadOnlyList<ShutdownEvent> ReadRecent()
    {
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System[(EventID=41 or EventID=109 or EventID=1074 or EventID=6005 or EventID=6006 or EventID=6008)" +
            " and TimeCreated[timediff(@SystemTime) <= 2592000000]]]")
        {
            ReverseDirection = true,
        };

        var result = new List<ShutdownEvent>();
        using var reader = new EventLogReader(query);
        while (result.Count < MaxEvents && reader.ReadEvent() is { } record)
        {
            using (record)
            {
                if (record.TimeCreated is not { } time)
                {
                    continue;
                }

                var data = record.Properties.Select(p => p.Value?.ToString() ?? "").ToList();
                result.Add(new ShutdownEvent(time, record.ProviderName ?? "", record.Id, data, ReadNamedData(record)));
            }
        }

        return result;
    }

    private static Dictionary<string, string> ReadNamedData(EventRecord record)
    {
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var xml = XDocument.Parse(record.ToXml());
            foreach (var element in xml.Descendants().Where(e => e.Name.LocalName == "Data"))
            {
                if (element.Attribute("Name")?.Value is { } name)
                {
                    named[name] = element.Value;
                }
            }
        }
        catch
        {
            // Named fields are a bonus; positional data and the event IDs themselves are enough.
        }

        return named;
    }
}

public enum ShutdownKind { Clean, Unexpected, Unknown }

public sealed record PreviousShutdownInfo(
    ShutdownKind Kind,
    DateTime? When,
    uint? BugcheckCode,
    string? CleanDetail,
    UpsSnapshot? Ups,
    DateTime? BootTime)
{
    public bool IsUnclean => Kind == ShutdownKind.Unexpected;

    /// <summary>
    /// Text for the alert history, or null when the shutdown isn't worth an entry there. An
    /// ordinary orderly shutdown (someone turned the machine off) is not an incident; an abrupt
    /// one is, and so is an orderly one forced by an empty UPS battery — that is the tail end of a
    /// power outage and the daily summary should say so.
    /// </summary>
    public string? HistoryDescription()
    {
        if (Kind == ShutdownKind.Unexpected)
        {
            var text = BugcheckCode is > 0 ? Strings.ShutdownEvent_Bugcheck(BugcheckCode.Value) : Strings.ShutdownEvent_Unclean;
            return Ups is { OnBattery: true } ? text + Strings.ShutdownEvent_OnBatterySuffix(Ups.ChargePercent) : text;
        }

        return Kind == ShutdownKind.Clean && Ups is { OnBattery: true }
            ? Strings.ShutdownEvent_LowBattery(Ups.ChargePercent)
            : null;
    }

    /// <summary>The lines for the startup notification (and the log): the verdict, then the UPS context if any.</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>();
        lines.Add(Kind switch
        {
            ShutdownKind.Clean => Strings.Shutdown_Clean(When, CleanDetail),
            ShutdownKind.Unexpected when BugcheckCode is > 0 => Strings.Shutdown_Bugcheck(BugcheckCode.Value, When),
            ShutdownKind.Unexpected => Strings.Shutdown_Unexpected(When),
            _ => Strings.Shutdown_Unknown,
        });

        if (Ups is { } ups && Kind != ShutdownKind.Unknown)
        {
            var observed = ups.ObservedAt.LocalDateTime;
            if (ups.OnBattery)
            {
                lines.Add(Kind == ShutdownKind.Clean
                    ? Strings.Shutdown_UpsCleanOnBattery(ups.ChargePercent, observed)
                    : Strings.Shutdown_UpsUncleanOnBattery(ups.ChargePercent, observed));
            }
            else if (Kind == ShutdownKind.Unexpected)
            {
                lines.Add(Strings.Shutdown_UpsUncleanOnMains(ups.ChargePercent, observed));
            }
        }

        return lines;
    }
}

/// <summary>
/// Explains how the previous session of the machine ended, for the startup notification:
/// orderly (and who/why), abrupt (power cut, forced reset, blue screen), or unknown — plus, when a
/// UPS was being monitored, whether the machine was running on battery when it stopped.
///
/// Windows already records the facts (see <see cref="WindowsShutdownEventSource"/>); this only
/// interprets them. The one thing Windows can't say is "it was on battery", which comes from
/// <see cref="RuntimeStateStore.Previous"/>. Reporting applies only to the first service start after
/// a boot: a service restart on a machine that has been up for hours has no shutdown to report.
/// </summary>
public sealed class PreviousShutdownAnalyzer
{
    private static readonly TimeSpan MaxBootToServiceStart = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan UncleanMarkerWindowBefore = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UncleanMarkerWindowAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan InitiatorLookback = TimeSpan.FromMinutes(10);

    private const string KernelPower = "Microsoft-Windows-Kernel-Power";

    private readonly IShutdownEventSource _source;
    private readonly RuntimeStateStore _state;
    private readonly UpsOptions _upsOptions;
    private readonly ILogger<PreviousShutdownAnalyzer> _logger;

    public PreviousShutdownAnalyzer(
        IShutdownEventSource source,
        RuntimeStateStore state,
        IOptions<MonitoringOptions> options,
        ILogger<PreviousShutdownAnalyzer> logger)
    {
        _source = source;
        _state = state;
        _upsOptions = options.Value.Ups;
        _logger = logger;
    }

    /// <summary>
    /// Null = nothing to report (not the first start since boot, or analysis failed). With
    /// <paramref name="onlyIfJustBooted"/> false (the --test-ups command) the last shutdown is
    /// analyzed regardless of how long ago the machine booted.
    /// </summary>
    public PreviousShutdownInfo? Analyze(DateTime processStartLocal, bool onlyIfJustBooted = true)
    {
        try
        {
            return AnalyzeCore(_source.ReadRecent(), processStartLocal, onlyIfJustBooted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_PreviousShutdownFailed);
            return null;
        }
    }

    private PreviousShutdownInfo? AnalyzeCore(IReadOnlyList<ShutdownEvent> events, DateTime processStartLocal, bool onlyIfJustBooted)
    {
        var boot = events.FirstOrDefault(e => e.Provider == "EventLog" && e.Id == 6005);
        if (boot is null)
        {
            return onlyIfJustBooted ? null : Build(ShutdownKind.Unknown, null, null, null, null);
        }

        if (onlyIfJustBooted && (processStartLocal < boot.TimeLocal - UncleanMarkerWindowBefore || processStartLocal - boot.TimeLocal > MaxBootToServiceStart))
        {
            return null;
        }

        // If the previous run of this service was alive AFTER the machine booted, this is not the first
        // start since boot (the service was just restarted): there is no shutdown to report, and the
        // saved heartbeat/UPS state belong to the current boot session, not to the one that ended.
        var lastSeen = _state.Previous?.LastSeenAt?.LocalDateTime;
        var stateIsFromThisBoot = lastSeen is { } seen && seen > boot.TimeLocal;
        if (stateIsFromThisBoot)
        {
            if (onlyIfJustBooted)
            {
                return null;
            }

            lastSeen = null; // diagnostics still analyze the shutdown, but must not show a time after the boot
        }

        // Windows writes these two right after boot when the previous session did not end cleanly.
        bool NearBoot(ShutdownEvent e) =>
            e.TimeLocal >= boot.TimeLocal - UncleanMarkerWindowBefore && e.TimeLocal <= boot.TimeLocal + UncleanMarkerWindowAfter;

        var unexpectedLog = events.FirstOrDefault(e => e.Provider == "EventLog" && e.Id == 6008 && NearBoot(e));
        var kernelPower41 = events.FirstOrDefault(e => e.Provider == KernelPower && e.Id == 41 && NearBoot(e));
        if (unexpectedLog is not null || kernelPower41 is not null)
        {
            uint? bugcheck = kernelPower41 is not null
                && kernelPower41.Named.TryGetValue("BugcheckCode", out var raw)
                && uint.TryParse(raw, out var code) ? code : null;
            return Build(ShutdownKind.Unexpected, lastSeen, bugcheck, null, boot.TimeLocal, stateIsFromThisBoot);
        }

        var stop = events.FirstOrDefault(e => e.Provider == "EventLog" && e.Id == 6006 && e.TimeLocal < boot.TimeLocal);
        if (stop is null)
        {
            return Build(ShutdownKind.Unknown, null, null, null, boot.TimeLocal, stateIsFromThisBoot);
        }

        return Build(ShutdownKind.Clean, stop.TimeLocal, null, DescribeInitiator(events, stop), boot.TimeLocal, stateIsFromThisBoot);
    }

    private PreviousShutdownInfo Build(ShutdownKind kind, DateTime? when, uint? bugcheck, string? detail, DateTime? bootTime, bool stateIsFromThisBoot = false) =>
        new(kind, when, bugcheck, detail, _upsOptions.Enabled && !stateIsFromThisBoot ? _state.Previous?.Ups : null, bootTime);

    /// <summary>
    /// The earliest 1074 shortly before the log service stopped is the request that started the
    /// shutdown (a restart usually logs a second one a moment later, on behalf of SYSTEM). With no
    /// 1074 at all, the kernel itself started it — which is what a critical-battery shutdown looks like.
    /// </summary>
    private static string? DescribeInitiator(IReadOnlyList<ShutdownEvent> events, ShutdownEvent stop)
    {
        var initiator = events
            .Where(e => e.Provider == "User32" && e.Id == 1074 && e.Data.Count >= 7
                        && e.TimeLocal <= stop.TimeLocal && stop.TimeLocal - e.TimeLocal <= InitiatorLookback)
            .OrderBy(e => e.TimeLocal)
            .FirstOrDefault();
        if (initiator is not null)
        {
            return Strings.Shutdown_InitiatedByUser(initiator.Data[6], initiator.Data[0], initiator.Data[4], initiator.Data[2]);
        }

        var kernel = events.FirstOrDefault(e => e.Provider == KernelPower && e.Id == 109
            && Math.Abs((e.TimeLocal - stop.TimeLocal).TotalMinutes) <= 2);
        if (kernel is not null)
        {
            kernel.Named.TryGetValue("ShutdownActionType", out var action);
            kernel.Named.TryGetValue("ShutdownReason", out var reason);
            return Strings.Shutdown_InitiatedBySystem(action, reason);
        }

        return null;
    }
}
