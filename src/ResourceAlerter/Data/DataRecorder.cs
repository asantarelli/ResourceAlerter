using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;

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
            _logger.LogError(ex, "Failed to initialize the SQLite database at {Path}; data recording is disabled for this run", _dbPath);
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

        _logger.LogInformation("SQLite data recording initialized at {Path} (retention {Days} days)", _dbPath, _options.RetentionDays);
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
            _logger.LogWarning(ex, "Could not adjust ACL on {Directory}; the Viewer may not be able to read the database without elevation", directory);
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
                _logger.LogWarning(ex, "Failed to record {Count} samples", samples.Count);
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
                _logger.LogWarning(ex, "Failed to record alert start for {Monitor}/{Subject}", monitor, subject);
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
                _logger.LogWarning(ex, "Failed to record alert resolution for {Monitor}/{Subject}", monitor, subject);
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
                _logger.LogWarning(ex, "Failed to list recorded series");
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
                _logger.LogWarning(ex, "Failed to read samples for {Monitor}/{Subject}", monitor, subject);
            }
            return result;
        }
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
                _logger.LogWarning(ex, "Failed to read alert events");
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
                _logger.LogInformation("Purged {Count} database rows older than {Days} days", removed, _options.RetentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database purge failed");
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
