using Microsoft.Data.Sqlite;
using ResourceAlerter.Localization;

namespace ResourceAlerter.Viewer;

public sealed record SeriesKey(string Monitor, string Subject, string? Unit)
{
    public override string ToString() => $"{Strings.MonitorDisplayName(Monitor)} — {Strings.SubjectDisplayName(Monitor, Subject)}";
}

public sealed record SamplePoint(DateTimeOffset Timestamp, double Value);

public sealed record AlertMark(DateTimeOffset StartedAt, DateTimeOffset? ResolvedAt);

/// <summary>
/// Read-only access to the service's SQLite database. Opens per-query, short-lived
/// connections in ReadWrite mode: WAL readers need write access to the -shm coordination
/// file (the service grants Users Modify on the data directory for exactly this reason),
/// but this class never issues an INSERT/UPDATE — the service is the only writer.
/// </summary>
public sealed class DataReader
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public DataReader(string databasePath)
    {
        DatabasePath = databasePath;
        _connectionString = $"Data Source={databasePath}";
    }

    public bool DatabaseExists() => File.Exists(DatabasePath);

    public IReadOnlyList<SeriesKey> GetSeries()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Monitor, Subject, MAX(Unit) FROM Samples GROUP BY Monitor, Subject ORDER BY Monitor, Subject";
        using var reader = cmd.ExecuteReader();
        var result = new List<SeriesKey>();
        while (reader.Read())
        {
            result.Add(new SeriesKey(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }
        return result;
    }

    public (SamplePoint? Latest, IReadOnlyList<SamplePoint> Last24h) GetSamples(SeriesKey series)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var from = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Timestamp, Value FROM Samples
            WHERE Monitor = $mon AND Subject = $sub AND Timestamp >= $from
            ORDER BY Timestamp
            """;
        cmd.Parameters.AddWithValue("$mon", series.Monitor);
        cmd.Parameters.AddWithValue("$sub", series.Subject);
        cmd.Parameters.AddWithValue("$from", from);

        var points = new List<SamplePoint>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                points.Add(new SamplePoint(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetDouble(1)));
            }
        }

        var latest = points.Count > 0 ? points[^1] : GetLatestEver(connection, series);
        return (latest, points);
    }

    private static SamplePoint? GetLatestEver(SqliteConnection connection, SeriesKey series)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT Timestamp, Value FROM Samples
            WHERE Monitor = $mon AND Subject = $sub
            ORDER BY Timestamp DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$mon", series.Monitor);
        cmd.Parameters.AddWithValue("$sub", series.Subject);
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new SamplePoint(DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)), reader.GetDouble(1))
            : null;
    }

    public IReadOnlyList<AlertMark> GetAlerts24h(SeriesKey series)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT StartedAt, ResolvedAt FROM AlertEvents
            WHERE Monitor = $mon AND Subject = $sub AND StartedAt >= $from
            ORDER BY StartedAt
            """;
        cmd.Parameters.AddWithValue("$mon", series.Monitor);
        cmd.Parameters.AddWithValue("$sub", series.Subject);
        cmd.Parameters.AddWithValue("$from", DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds());
        using var reader = cmd.ExecuteReader();
        var result = new List<AlertMark>();
        while (reader.Read())
        {
            result.Add(new AlertMark(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0)),
                reader.IsDBNull(1) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1))));
        }
        return result;
    }
}
