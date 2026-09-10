using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ResourceAlerter.Configuration;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Monitors;

/// <summary>
/// Health checks against a SQL Server INSTANCE (not just one database on it), connecting via
/// credentials read from an EXISTING app's own connection-settings INI file (e.g. a Clarion
/// app's "fm3&lt;App&gt;.ini") rather than storing a SQL login/password in ResourceAlerter's own
/// config — see <see cref="SqlServerOptions.IniFilePath"/>. The INI is only used to obtain
/// access to the server; the database it names (e.g. "Pasil") is just where that particular app
/// happens to live, not the scope of what gets monitored here — every check queries an
/// instance-wide DMV/DBCC command that sees the whole server regardless of which database the
/// connection's current catalog is (<see cref="CheckLogSpace"/> in particular reports on every
/// database on the instance, not just the one in the INI). The INI is re-read on every check
/// (cheap, small text file), so a password rotated in the source app is picked up automatically.
///
/// Checks run on their own cadence (<see cref="SqlServerOptions.CheckIntervalSeconds"/>),
/// decoupled from the main polling loop — same reasoning as <see cref="NetworkMonitor"/>'s ping
/// interval: a SQL round trip is heavier than an in-process OS read. If the connection itself
/// fails, that's reported as a real out-of-range "Connectivity" alert (not Unavailable — the
/// whole point is to catch the instance being down). Once connected, each individual query is
/// wrapped separately so one failing (e.g. a lower-privilege login lacking permission for a
/// specific DMV) doesn't take down the others — same granular-unavailable pattern used
/// throughout this project for a missing sensor.
/// </summary>
public sealed class SqlServerMonitor : IHealthMonitor
{
    public string Name => "SqlServer";
    public MonitorOptionsBase Options => _options;

    private readonly SqlServerOptions _options;
    private readonly ILogger<SqlServerMonitor> _logger;

    private DateTimeOffset _lastCheckAt = DateTimeOffset.MinValue;
    private IReadOnlyList<MonitorResult>? _lastResult;
    private DateTime _lastErrorLogCheckAt = DateTime.MinValue;

    private static readonly string[] SystemDatabases = { "master", "model", "msdb", "tempdb" };

    public SqlServerMonitor(IOptions<MonitoringOptions> options, ILogger<SqlServerMonitor> logger)
    {
        _options = options.Value.SqlServer;
        _logger = logger;
    }

    public IReadOnlyList<MonitorResult> Check()
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastResult is null || now - _lastCheckAt >= TimeSpan.FromSeconds(_options.CheckIntervalSeconds))
        {
            _lastCheckAt = now;
            _lastResult = DoCheck();
        }

        return _lastResult!;
    }

    private List<MonitorResult> DoCheck()
    {
        if (string.IsNullOrWhiteSpace(_options.IniFilePath) || string.IsNullOrWhiteSpace(_options.IniSection))
        {
            return new List<MonitorResult>
            {
                Unavailable(Strings.SqlServer_ConnectivitySubjectKey, Strings.Unavailable_SqlServerNotConfigured),
            };
        }

        var ini = IniFile.ReadSection(_options.IniFilePath, _options.IniSection);
        if (ini.Count == 0)
        {
            return new List<MonitorResult>
            {
                Unavailable(Strings.SqlServer_ConnectivitySubjectKey,
                    Strings.Unavailable_SqlServerIniSectionEmpty(_options.IniFilePath, _options.IniSection)),
            };
        }

        string connectionString;
        try
        {
            connectionString = BuildConnectionString(ini);
        }
        catch (Exception ex)
        {
            return new List<MonitorResult>
            {
                Unavailable(Strings.SqlServer_ConnectivitySubjectKey, Strings.Unavailable_SqlServerCheckFailed(ex.Message)),
            };
        }

        var results = new List<MonitorResult>();
        using var connection = new SqlConnection(connectionString);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            connection.Open();
            using var pingCmd = connection.CreateCommand();
            pingCmd.CommandText = "SELECT 1";
            pingCmd.CommandTimeout = _options.ConnectTimeoutSeconds;
            pingCmd.ExecuteScalar();
            stopwatch.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, Strings.Log_SqlServerConnectFailed);
            results.Add(new MonitorResult
            {
                Subject = Strings.SqlServer_ConnectivitySubjectKey,
                InRange = false,
                DisplayValue = Strings.SqlServer_ConnectFailed(ex.Message),
                DisplayThreshold = Strings.SqlServer_ConnectivityThreshold,
            });
            return results; // nothing past this point is reachable without a connection
        }

        results.Add(new MonitorResult
        {
            Subject = Strings.SqlServer_ConnectivitySubjectKey,
            InRange = true,
            DisplayValue = Strings.SqlServer_Connected(stopwatch.Elapsed.TotalMilliseconds),
            NumericValue = stopwatch.Elapsed.TotalMilliseconds,
            Unit = "ms",
            DisplayThreshold = Strings.SqlServer_ConnectivityThreshold,
        });

        TryAddSingle(results, connection, Strings.SqlServer_MemorySubjectKey, CheckMemory);
        TryAddSingle(results, connection, Strings.SqlServer_ConnectionsSubjectKey, CheckConnections);
        TryAddSingle(results, connection, Strings.SqlServer_BlockingSubjectKey, CheckBlocking);
        TryAddMany(results, connection, Strings.SqlServer_LogSpaceSubjectKey, CheckLogSpace);
        TryAddSingle(results, connection, Strings.SqlServer_ErrorLogSubjectKey, CheckErrorLog);

        return results;
    }

    private void TryAddSingle(List<MonitorResult> results, SqlConnection connection, string subjectKey, Func<SqlConnection, MonitorResult> check)
    {
        try
        {
            results.Add(check(connection));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, Strings.Log_SqlServerCheckFailed, subjectKey);
            results.Add(Unavailable(subjectKey, Strings.Unavailable_SqlServerCheckFailed(ex.Message)));
        }
    }

    private void TryAddMany(List<MonitorResult> results, SqlConnection connection, string fallbackSubjectKey, Func<SqlConnection, List<MonitorResult>> check)
    {
        try
        {
            results.AddRange(check(connection));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, Strings.Log_SqlServerCheckFailed, fallbackSubjectKey);
            results.Add(Unavailable(fallbackSubjectKey, Strings.Unavailable_SqlServerCheckFailed(ex.Message)));
        }
    }

    /// <summary>
    /// Deliberately NOT <c>sys.dm_os_process_memory.memory_utilization_percentage</c> — despite
    /// the name, that column measures how much of SQL Server's OWN committed memory is in its
    /// working set, not how close the machine is to running out of RAM. In practice it sits at
    /// or near 100% almost all the time on a healthy instance (confirmed against a real SQL
    /// Express box during development) — using it as an alert threshold would false-positive on
    /// nearly every real installation from the first check. Instead this reports what fraction
    /// of the MACHINE's total physical RAM SQL Server is currently using, directly comparable to
    /// how the OS-level Memory monitor already works.
    /// </summary>
    private MonitorResult CheckMemory(SqlConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT pm.physical_memory_in_use_kb, sm.total_physical_memory_kb
            FROM sys.dm_os_process_memory pm
            CROSS JOIN sys.dm_os_sys_memory sm
            """;
        cmd.CommandTimeout = _options.ConnectTimeoutSeconds;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var usedKb = Convert.ToDouble(reader.GetValue(0));
        var totalKb = Convert.ToDouble(reader.GetValue(1));
        var percent = totalKb <= 0 ? 0 : usedKb * 100.0 / totalKb;
        var outOfRange = percent > _options.MemoryUtilizationThresholdPercent;

        return new MonitorResult
        {
            Subject = Strings.SqlServer_MemorySubjectKey,
            InRange = !outOfRange,
            DisplayValue = Strings.SqlServer_MemoryValue(percent, usedKb / 1024.0),
            NumericValue = percent,
            Unit = "%",
            DisplayThreshold = Strings.SqlServer_MemoryThreshold(_options.MemoryUtilizationThresholdPercent),
        };
    }

    private MonitorResult CheckConnections(SqlConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_connections";
        cmd.CommandTimeout = _options.ConnectTimeoutSeconds;
        var count = Convert.ToInt32(cmd.ExecuteScalar());
        var outOfRange = count > _options.MaxConnectionsThreshold;

        return new MonitorResult
        {
            Subject = Strings.SqlServer_ConnectionsSubjectKey,
            InRange = !outOfRange,
            DisplayValue = Strings.SqlServer_ConnectionsValue(count),
            NumericValue = count,
            Unit = "connections",
            DisplayThreshold = Strings.SqlServer_ConnectionsThreshold(_options.MaxConnectionsThreshold),
        };
    }

    private MonitorResult CheckBlocking(SqlConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) AS BlockedCount, ISNULL(MAX(DATEDIFF(SECOND, r.start_time, GETDATE())), 0) AS MaxBlockedSeconds
            FROM sys.dm_exec_requests r
            WHERE r.blocking_session_id <> 0
            """;
        cmd.CommandTimeout = _options.ConnectTimeoutSeconds;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        var blockedCount = reader.GetInt32(0);
        var maxBlockedSeconds = Convert.ToDouble(reader.GetValue(1));
        var outOfRange = blockedCount > 0 && maxBlockedSeconds > _options.MaxBlockingSeconds;

        return new MonitorResult
        {
            Subject = Strings.SqlServer_BlockingSubjectKey,
            InRange = !outOfRange,
            DisplayValue = Strings.SqlServer_BlockingValue(blockedCount, maxBlockedSeconds),
            NumericValue = maxBlockedSeconds,
            Unit = "s",
            DisplayThreshold = Strings.SqlServer_BlockingThreshold(_options.MaxBlockingSeconds),
        };
    }

    /// <summary>
    /// DBCC SQLPERF(LOGSPACE) reports every database's transaction log size/usage in one call,
    /// with no need to switch database context per-DB (unlike data-file space, which needs a
    /// per-database query — out of scope for now, see SqlServerOptions doc comment). System
    /// databases are skipped: their log space isn't actionable for an app-focused monitor and
    /// would just clutter the Viewer's series list.
    /// </summary>
    private List<MonitorResult> CheckLogSpace(SqlConnection connection)
    {
        var results = new List<MonitorResult>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DBCC SQLPERF(LOGSPACE)";
        cmd.CommandTimeout = _options.ConnectTimeoutSeconds;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var databaseName = reader.GetString(0);
            if (SystemDatabases.Contains(databaseName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var sizeMb = Convert.ToDouble(reader.GetValue(1));
            var percentUsed = Convert.ToDouble(reader.GetValue(2));
            var outOfRange = percentUsed > _options.LogSpaceUsedThresholdPercent;

            results.Add(new MonitorResult
            {
                Subject = databaseName,
                InRange = !outOfRange,
                DisplayValue = Strings.SqlServer_LogSpaceValue(percentUsed, sizeMb),
                NumericValue = percentUsed,
                Unit = "%",
                DisplayThreshold = Strings.SqlServer_LogSpaceThreshold(_options.LogSpaceUsedThresholdPercent),
            });
        }

        return results;
    }

    /// <summary>
    /// Reports new SQL Server error-log entries (lines starting "Error:", SQL Server's own
    /// format for real errors) since the last check — a delta, same idea as NetworkMonitor's
    /// interface-error counter. Requires the configured login to have permission to run
    /// xp_readerrorlog (sysadmin, or an explicit GRANT); TryAddSingle silently marks this
    /// Unavailable otherwise. Includes a few snippets of the actual error text in DisplayValue
    /// so the alert is directly actionable, not just a bare count.
    /// </summary>
    private MonitorResult CheckErrorLog(SqlConnection connection)
    {
        var now = DateTime.Now; // SQL Server's error log timestamps are the server's own local time
        var from = _lastErrorLogCheckAt == DateTime.MinValue ? now.AddSeconds(-_options.CheckIntervalSeconds) : _lastErrorLogCheckAt;
        _lastErrorLogCheckAt = now;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "EXEC sys.xp_readerrorlog 0, 1, @search, NULL, @from, @to, N'desc'";
        cmd.CommandTimeout = _options.ConnectTimeoutSeconds;
        cmd.Parameters.AddWithValue("@search", "Error:");
        cmd.Parameters.AddWithValue("@from", from);
        cmd.Parameters.AddWithValue("@to", now);

        var snippets = new List<string>();
        var count = 0;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                count++;
                if (snippets.Count < 3)
                {
                    var text = reader.GetString(2); // columns: LogDate, ProcessInfo, Text
                    snippets.Add(text.Length > 120 ? text[..120] + "…" : text);
                }
            }
        }

        var outOfRange = count > _options.MaxNewErrorLogEntriesPerInterval;
        return new MonitorResult
        {
            Subject = Strings.SqlServer_ErrorLogSubjectKey,
            InRange = !outOfRange,
            DisplayValue = Strings.SqlServer_ErrorLogValue(count, snippets),
            NumericValue = count,
            Unit = "errors",
            DisplayThreshold = Strings.SqlServer_ErrorLogThreshold(_options.MaxNewErrorLogEntriesPerInterval),
        };
    }

    private SqlConnectionStringBuilder BuildConnectionStringBuilder(Dictionary<string, string> ini)
    {
        var server = ini.GetValueOrDefault("Server", "");
        if (string.IsNullOrWhiteSpace(server))
        {
            throw new InvalidOperationException("INI section has no 'Server' value.");
        }

        var port = ini.GetValueOrDefault("Port", "");
        var dataSource = string.IsNullOrWhiteSpace(port) || port == "0" ? server : $"{server},{port}";

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = dataSource,
            // Deliberately NOT ini["DatabaseName"] (the app's own database, e.g. "Pasil") -- every
            // check here is instance-wide (sys.dm_os_process_memory, sys.dm_exec_connections,
            // sys.dm_exec_requests, DBCC SQLPERF(LOGSPACE), xp_readerrorlog all see the whole
            // server regardless of the connection's current database), so this monitor should
            // watch the SQL SERVER INSTANCE, not just the one app database named in the INI --
            // there can be (and in practice usually are) several other databases on the same
            // instance that also need watching. Using the app's database as InitialCatalog would
            // mean a problem with JUST that one database (offline, restricted, dropped) breaks the
            // connection entirely and silently stops monitoring every other database on the
            // server too. "master" is a system database that's essentially always available and
            // connectable regardless of any application database's state.
            InitialCatalog = "master",
            // Typical on-prem SQL Express instances used by small shops don't have a properly
            // chained TLS certificate configured -- Microsoft.Data.SqlClient defaults to
            // Encrypt=true, which would otherwise fail to connect at all. Encrypt the wire but
            // don't validate the certificate chain, matching Microsoft's own documented
            // workaround for exactly this scenario.
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = _options.ConnectTimeoutSeconds,
        };

        if (ini.GetValueOrDefault("UseWindowsAuthentication", "0") == "1")
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = ini.GetValueOrDefault("UserName", "");
            builder.Password = ini.GetValueOrDefault("Password", "");
        }

        return builder;
    }

    private string BuildConnectionString(Dictionary<string, string> ini) => BuildConnectionStringBuilder(ini).ConnectionString;

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
