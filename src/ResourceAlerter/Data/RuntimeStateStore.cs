using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Data;

/// <summary>The UPS as last observed: enough to tell, after a reboot, whether the machine was on battery when it stopped.</summary>
public sealed record UpsSnapshot(bool OnBattery, int? ChargePercent, int? RuntimeMinutes, DateTimeOffset ObservedAt);

/// <param name="LastSeenAt">Heartbeat: the last moment this service was known to be alive.</param>
/// <param name="Ups">Null when no UPS was being monitored.</param>
public sealed record RuntimeState(DateTimeOffset? LastSeenAt, UpsSnapshot? Ups);

/// <summary>
/// A tiny JSON file next to the SQLite database holding "what was the machine doing the last time
/// we looked": a heartbeat plus the UPS state. It exists for one reason — after a reboot, to tell
/// whether the previous shutdown happened while running on battery, which the Windows event log
/// can't say. The file is written with temp-file-then-replace because the moments that matter
/// most (a power cut) are exactly when a half-written file would otherwise be left behind.
///
/// <see cref="Previous"/> is read once at construction, before anything in this process can
/// overwrite the file, so the analysis of the previous session is never racing the new one.
/// All failures are logged and swallowed: state tracking must never take monitoring down.
/// </summary>
public sealed class RuntimeStateStore
{
    private const int HeartbeatFlushSeconds = 30;

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private DateTimeOffset? _lastSeen;
    private UpsSnapshot? _ups;
    private DateTimeOffset _lastFlush = DateTimeOffset.MinValue;

    /// <summary>The state left by the previous run of the service; null if there was none or it was unreadable.</summary>
    public RuntimeState? Previous { get; }

    public RuntimeStateStore(IOptions<DatabaseOptions> options, ILogger<RuntimeStateStore> logger)
        : this(Path.Combine(Path.GetDirectoryName(options.Value.GetExpandedPath()) ?? AppContext.BaseDirectory, "runtime-state.json"), logger)
    {
    }

    private RuntimeStateStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        Previous = TryLoad();
    }

    public static RuntimeStateStore ForPath(string path, ILogger logger) => new(path, logger);

    private RuntimeState? TryLoad()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(_path), JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_RuntimeStateReadFailed, _path);
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    /// <summary>Heartbeat; called once per polling cycle. Flushed to disk at most every 30 seconds.</summary>
    public void Touch()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _lastSeen = now;
            FlushIfDue(now, force: false);
        }
    }

    /// <summary>
    /// Records the UPS state (null = no UPS visible). A change of source is flushed immediately, and
    /// so is every reading while running on battery — that is when the machine may stop at any moment.
    /// </summary>
    public void RecordUps(UpsSnapshot? snapshot)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var changed = (snapshot is null) != (_ups is null) || snapshot?.OnBattery != _ups?.OnBattery;
            _ups = snapshot;
            _lastSeen = now;
            FlushIfDue(now, force: changed || snapshot?.OnBattery == true);
        }
    }

    private void FlushIfDue(DateTimeOffset now, bool force)
    {
        if (!force && (now - _lastFlush).TotalSeconds < HeartbeatFlushSeconds)
        {
            return;
        }

        _lastFlush = now;
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new RuntimeState(_lastSeen, _ups), JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_RuntimeStateWriteFailed, _path);
        }
    }
}
