using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Data;

/// <summary>One recorded reading, ready for insertion.</summary>
public sealed record SampleRecord(string Monitor, string Subject, double Value, string? Unit);

/// <summary>A completed or ongoing alert period, as read back for charts/summaries.</summary>
public sealed record AlertEventRecord(
    long Id, string Monitor, string Subject,
    DateTimeOffset StartedAt, DateTimeOffset? ResolvedAt,
    string? DetectedValue, string? Threshold);

/// <summary>A charted time series point.</summary>
public sealed record SamplePoint(DateTimeOffset Timestamp, double Value);

/// <summary>
/// Owns the SQLite database: every poll cycle's numeric readings go into Samples, and alert
/// trigger/resolve transitions go into AlertEvents. WAL journaling so the Viewer (a separate
/// process, running as a regular user) can read while the service writes. All writes are
/// best-effort: a database problem is logged and must never take monitoring down.
/// </summary>
public sealed class DataRecorder : IDisposable
{
    private readonly DatabaseOptions _options;
    private readonly ILogger<DataRecorder> _logger;
    private readonly string _dbPath;
    private readonly object _gate = new();
    private SqliteConnection? _connection;
    private DateOnly _lastPurgeDay;

    public string DatabasePath => _dbPath;

    /// <summary>False if the database failed to open — recording is silently disabled in that
    /// case (never take monitoring down over it), but <see cref="Worker"/> surfaces this in the
    /// startup mail so it's never a silent failure to the admin.</summary>
    public bool IsAvailable => _connection is not null;

    public string? InitializationError { get; private set; }

    public DataRecorder(IOptions<DatabaseOptions> options, ILogger<DataRecorder> logger)
    {
        _options = options.Value;
        _logger = logger;
        _dbPath = _options.GetExpandedPath();

        try
        {
            Initialize();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, Strings.Log_DbInitFailed, _dbPath);
            InitializationError = ex.Message;
            _connection = null;
        }
    }

    private void Initialize()
    {
        var directory = Path.GetDirectoryName(_dbPath)!;
        Directory.CreateDirectory(directory);
        GrantUsersReadAccess(directory);

        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS Samples (
                Id        INTEGER PRIMARY KEY,
                Timestamp INTEGER NOT NULL,  -- unix seconds, UTC
                Monitor   TEXT NOT NULL,
                Subject   TEXT NOT NULL,
                Value     REAL NOT NULL,
                Unit      TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_Samples_Series_Time
                ON Samples(Monitor, Subject, Timestamp);

            CREATE TABLE IF NOT EXISTS AlertEvents (
                Id            INTEGER PRIMARY KEY,
                Monitor       TEXT NOT NULL,
                Subject       TEXT NOT NULL,
                StartedAt     INTEGER NOT NULL,  -- unix seconds, UTC
                ResolvedAt    INTEGER,
                DetectedValue TEXT,
                Threshold     TEXT
            );
            CREATE INDEX IF NOT EXISTS IX_AlertEvents_Time ON AlertEvents(StartedAt);
            """;
        cmd.ExecuteNonQuery();

        _logger.LogInformation(Strings.Log_DbInitialized, _dbPath, _options.RetentionDays);
    }

    /// <summary>
    /// The service runs as LocalSystem, so files it creates under ProgramData are not
    /// modifiable by regular users — but SQLite WAL readers need write access to the -shm
    /// coordination file. Granting Modify on the data directory to BUILTIN\Users lets the
    /// Viewer participate in WAL reads without running elevated.
    /// </summary>
    private void GrantUsersReadAccess(string directory)
    {
        try
        {
            var dirInfo = new DirectoryInfo(directory);
            var security = dirInfo.GetAccessControl();
            var usersSid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            security.AddAccessRule(new FileSystemAccessRule(
                usersSid,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            dirInfo.SetAccessControl(security);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_AclAdjustFailed, directory);
        }
    }

    public void RecordSamples(IReadOnlyList<SampleRecord> samples, DateTimeOffset timestamp)
    {
        if (_connection is null || samples.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                using var tx = _connection.BeginTransaction();
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO Samples (Timestamp, Monitor, Subject, Value, Unit) VALUES ($ts, $mon, $sub, $val, $unit)";
                var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);
                var pMon = cmd.CreateParameter(); pMon.ParameterName = "$mon"; cmd.Parameters.Add(pMon);
                var pSub = cmd.CreateParameter(); pSub.ParameterName = "$sub"; cmd.Parameters.Add(pSub);
                var pVal = cmd.CreateParameter(); pVal.ParameterName = "$val"; cmd.Parameters.Add(pVal);
                var pUnit = cmd.CreateParameter(); pUnit.ParameterName = "$unit"; cmd.Parameters.Add(pUnit);

                pTs.Value = timestamp.ToUnixTimeSeconds();
                foreach (var sample in samples)
                {
                    pMon.Value = sample.Monitor;
                    pSub.Value = sample.Subject;
                    pVal.Value = sample.Value;
                    pUnit.Value = (object?)sample.Unit ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, Strings.Log_RecordSamplesFailed, samples.Count);
            }

            PurgeIfDue();
        }
    }

    public void RecordAlertStart(string monitor, string subject, DateTimeOffset startedAt, string? detectedValue, string? threshold)
    {
        if (_connection is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO AlertEvents (Monitor, Subject, StartedAt, DetectedValue, Threshold)
                    VALUES ($mon, $sub, $ts, $val, $thr)
                    """;
                cmd.Parameters.AddWithValue("$mon", monitor);
                cmd.Parameters.AddWithValue("$sub", subject);
                cmd.Parameters.AddWithValue("$ts", startedAt.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$val", (object?)detectedValue ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$thr", (object?)threshold ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, Strings.Log_RecordAlertStartFailed, monitor, subject);
            }
        }
    }

    public void RecordAlertResolved(string monitor, string subject, DateTimeOffset resolvedAt)
    {
        if (_connection is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                using var cmd = _connection.CreateCommand();
                // Closes the most recent still-open event for this series.
                cmd.CommandText = """
                    UPDATE AlertEvents SET ResolvedAt = $ts
                    WHERE Id = (
                        SELECT Id FROM AlertEvents
                        WHERE Monitor = $mon AND Subject = $sub AND ResolvedAt IS NULL
                        ORDER BY StartedAt DESC LIMIT 1
                    )
                    """;
                cmd.Parameters.AddWithValue("$ts", resolvedAt.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$mon", monitor);
                cmd.Parameters.AddWithValue("$sub", subject);
                cmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, Strings.Log_RecordAlertResolvedFailed, monitor, subject);
            }
        }
    }

    public IReadOnlyList<(string Monitor, string Subject, string? Unit)> GetSeries()
    {
        if (_connection is null)
        {
            return Array.Empty<(string, string, string?)>();
        }

        lock (_gate)
        {
            var result = new List<(string, string, string?)>();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "SELECT Monitor, Subject, MAX(Unit) FROM Samples GROUP BY Monitor, Subject ORDER BY Monitor, Subject";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, Strings.Log_ListSeriesFailed);
            }
            return result;
        }
    }

    public IReadOnlyList<SamplePoint> GetSamples(string monitor, string subject, DateTimeOffset from, DateTimeOffset to)
    {
        if (_connection is null)
        {
            return Array.Empty<SamplePoint>();
        }

        lock (_gate)
        {
            var result = new List<SamplePoint>();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    SELECT Timestamp, Value FROM Samples
                    WHERE Monitor = $mon AND Subject = $sub AND Timestamp BETWEEN $from AND $to
                    ORDER BY Timestamp
                    """;
                cmd.Parameters.AddWithValue("$mon", monitor);
                cmd.Parameters.AddWithValue("$sub", subject);
                cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new SamplePoint(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetDouble(1)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, Strings.Log_ReadSamplesFailed, monitor, subject);
                return result;
            }
            return FillGaps(result);
        }
    }

    /// <summary>
    /// Inserts a zero-value point right after the last sample before a gap and another right
    /// before the first sample after it, so charts (Viewer and the daily-summary JPEGs) drop to
    /// 0 and back instead of drawing a straight (misleading) interpolated line across a period
    /// where nothing was actually recorded — e.g. the service was stopped, or a sensor was
    /// unavailable for a while. The gap threshold is derived from the series' own typical
    /// spacing (median inter-sample gap × 3) rather than hard-coded, since PollingIntervalSeconds
    /// is configurable up to an hour and a fixed threshold would false-positive on anyone using
    /// a slower interval.
    /// </summary>
    private static List<SamplePoint> FillGaps(List<SamplePoint> points)
    {
        if (points.Count < 3)
        {
            return points; // not enough data to establish a "typical" spacing
        }

        var deltas = new List<double>(points.Count - 1);
        for (var i = 1; i < points.Count; i++)
        {
            deltas.Add((points[i].Timestamp - points[i - 1].Timestamp).TotalSeconds);
        }
        deltas.Sort();
        var typicalGapSeconds = deltas[deltas.Count / 2];
        var thresholdSeconds = Math.Max(typicalGapSeconds * 3, 60);

        var filled = new List<SamplePoint>(points.Count) { points[0] };
        for (var i = 1; i < points.Count; i++)
        {
            var prev = points[i - 1];
            var curr = points[i];
            if ((curr.Timestamp - prev.Timestamp).TotalSeconds > thresholdSeconds)
            {
                filled.Add(new SamplePoint(prev.Timestamp.AddSeconds(1), 0));
                filled.Add(new SamplePoint(curr.Timestamp.AddSeconds(-1), 0));
            }
            filled.Add(curr);
        }
        return filled;
    }

    public IReadOnlyList<AlertEventRecord> GetAlertEvents(DateTimeOffset from, DateTimeOffset to)
    {
        if (_connection is null)
        {
            return Array.Empty<AlertEventRecord>();
        }

        lock (_gate)
        {
            var result = new List<AlertEventRecord>();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    SELECT Id, Monitor, Subject, StartedAt, ResolvedAt, DetectedValue, Threshold
                    FROM AlertEvents
                    WHERE StartedAt BETWEEN $from AND $to OR (ResolvedAt IS NULL AND StartedAt <= $to)
                    ORDER BY StartedAt
                    """;
                cmd.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new AlertEventRecord(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
                        reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
                        reader.IsDBNull(5) ? null : reader.GetString(5),
                        reader.IsDBNull(6) ? null : reader.GetString(6)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, Strings.Log_ReadAlertEventsFailed);
            }
            return result;
        }
    }

    private void PurgeIfDue()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _lastPurgeDay || _connection is null)
        {
            return;
        }

        _lastPurgeDay = today;
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-_options.RetentionDays).ToUnixTimeSeconds();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM Samples WHERE Timestamp < $cutoff; DELETE FROM AlertEvents WHERE StartedAt < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            var removed = cmd.ExecuteNonQuery();
            if (removed > 0)
            {
                _logger.LogInformation(Strings.Log_PurgedRows, removed, _options.RetentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_PurgeFailed);
        }
    }

    /// <summary>
    /// Deletes Samples/AlertEvents rows for (Monitor, Subject) pairs that are no longer produced
    /// by the current configuration — e.g. the admin switched Disk.Drives from "C:" to "D:", or
    /// (pre-v3.5.1) a language switch changed a translated Subject like "RAM física"/"Physical
    /// RAM" into a different string. Without this, old subjects live in the DB forever and the
    /// Viewer shows them as permanently-frozen, never-updating series alongside the real one.
    /// Only touches monitors present in <paramref name="activeSubjectsByMonitor"/> — a fully
    /// disabled monitor's history is left alone entirely (its Monitor name won't be a key in the
    /// dictionary), since going quiet is not the same as "no longer wanted".
    /// Called once at service startup (see <see cref="Worker"/>), not every poll cycle — this is
    /// a config-change cleanup, not a steady-state cost.
    /// </summary>
    public void PruneOrphanedSeries(IReadOnlyDictionary<string, HashSet<string>> activeSubjectsByMonitor)
    {
        if (_connection is null)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                var orphaned = new List<(string Monitor, string Subject)>();
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT Monitor, Subject FROM Samples
                        UNION
                        SELECT Monitor, Subject FROM AlertEvents
                        """;
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var monitor = reader.GetString(0);
                        var subject = reader.GetString(1);
                        if (activeSubjectsByMonitor.TryGetValue(monitor, out var activeSubjects) && !activeSubjects.Contains(subject))
                        {
                            orphaned.Add((monitor, subject));
                        }
                    }
                }

                if (orphaned.Count == 0)
                {
                    return;
                }

                using (var tx = _connection.BeginTransaction())
                using (var delCmd = _connection.CreateCommand())
                {
                    delCmd.Transaction = tx;
                    delCmd.CommandText = "DELETE FROM Samples WHERE Monitor = $mon AND Subject = $sub; DELETE FROM AlertEvents WHERE Monitor = $mon AND Subject = $sub;";
                    var pMon = delCmd.CreateParameter(); pMon.ParameterName = "$mon"; delCmd.Parameters.Add(pMon);
                    var pSub = delCmd.CreateParameter(); pSub.ParameterName = "$sub"; delCmd.Parameters.Add(pSub);

                    foreach (var (monitor, subject) in orphaned)
                    {
                        pMon.Value = monitor;
                        pSub.Value = subject;
                        delCmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }

                _logger.LogInformation(Strings.Log_PrunedOrphanedSeries,
                    orphaned.Count, string.Join(", ", orphaned.Select(o => $"{o.Monitor}/{o.Subject}")));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, Strings.Log_PruneOrphanedSeriesFailed);
            }
        }
    }

    /// <summary>
    /// Permanently deletes every Samples/AlertEvents row for one monitor category (e.g. "Disk")
    /// — a deliberate full wipe, unlike <see cref="PruneOrphanedSeries"/>'s cautious per-subject
    /// cleanup. Meant for the Settings screen's "Reset records" button: mainly useful right after
    /// an upgrade that changes a monitor's recorded unit (e.g. v4.0.0 switched Disk from GB to %
    /// free), so old and new data don't sit mixed in the same chart. Returns the number of rows
    /// deleted, or -1 on failure (distinct from a legitimate "0 rows" when nothing was recorded).
    /// </summary>
    public int ResetMonitorRecords(string monitorName)
    {
        if (_connection is null)
        {
            return -1;
        }

        lock (_gate)
        {
            try
            {
                using var tx = _connection.BeginTransaction();
                var removed = 0;

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM Samples WHERE Monitor = $mon";
                    cmd.Parameters.AddWithValue("$mon", monitorName);
                    removed += cmd.ExecuteNonQuery();
                }

                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM AlertEvents WHERE Monitor = $mon";
                    cmd.Parameters.AddWithValue("$mon", monitorName);
                    removed += cmd.ExecuteNonQuery();
                }

                tx.Commit();
                _logger.LogInformation(Strings.Log_ResetRecords, monitorName, removed);
                return removed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, Strings.Log_ResetRecordsFailed, monitorName);
                return -1;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
