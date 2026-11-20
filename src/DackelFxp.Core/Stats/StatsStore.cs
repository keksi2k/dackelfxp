using System.Globalization;
using DackelFxp.Core.Queue;
using Microsoft.Data.Sqlite;

namespace DackelFxp.Core.Stats;

public enum TransferResult { Ok = 0, Failed = 1, Aborted = 2 }

/// <summary>One finished transfer attempt of a file. <see cref="Bytes"/> counts only what moved in this attempt (not a resumed part).</summary>
public sealed record TransferRecord(DateTimeOffset Finished, TransferKind Kind, TransferResult Result,
    string SourceSite, string TargetSite, string FileName, long Bytes, TimeSpan Duration)
{
    public double Speed => Duration.TotalSeconds > 0 ? Bytes / Duration.TotalSeconds : 0;
}

/// <summary>Amount, completed files and summed transfer time of one direction.</summary>
public sealed record KindTotals(long Bytes, int Files, double Seconds)
{
    public static readonly KindTotals Empty = new(0, 0, 0);
    /// <summary>Average speed per transfer: bytes divided by the time the transfers took.</summary>
    public double AverageSpeed => Seconds > 0 ? Bytes / Seconds : 0;
}

public sealed record PeriodTotals(KindTotals Download, KindTotals Upload, KindTotals Fxp, int Failed)
{
    public long Bytes => Download.Bytes + Upload.Bytes + Fxp.Bytes;
    public int Files => Download.Files + Upload.Files + Fxp.Files;
}

public sealed record DayTotals(DateOnly Day, long Download, long Upload, long Fxp)
{
    public long Total => Download + Upload + Fxp;
}

public sealed record SiteTotals(string Site, long Bytes, int Files);

public sealed record StatsSnapshot(
    PeriodTotals Today,
    PeriodTotals Week,
    PeriodTotals Month,
    PeriodTotals Year,
    PeriodTotals All,
    IReadOnlyList<DayTotals> LastDays,
    IReadOnlyList<SiteTotals> TopSites,
    TransferRecord? Largest,
    TransferRecord? Fastest,
    DayTotals? BusiestDay,
    int? BusiestHour,
    DateTimeOffset? Since,
    int Aborted);

/// <summary>
/// Transfer statistics in a small SQLite database (stats.db in the configuration folder).
/// Every call opens its own pooled connection, so it can be used from any thread.
/// </summary>
public sealed class StatsStore
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public StatsStore(string file)
    {
        File = file;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = true }.ToString();
        using var db = Open();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS transfers (
                id          INTEGER PRIMARY KEY,
                finished    INTEGER NOT NULL, -- Unix time in ms (UTC)
                day         INTEGER NOT NULL, -- local date as yyyymmdd
                hour        INTEGER NOT NULL, -- local hour 0-23
                kind        INTEGER NOT NULL, -- TransferKind
                result      INTEGER NOT NULL, -- TransferResult
                source_site TEXT NOT NULL,
                target_site TEXT NOT NULL,
                file_name   TEXT NOT NULL,
                bytes       INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_transfers_day ON transfers(day);
            """);
        Execute(db, $"PRAGMA user_version = {SchemaVersion}");
    }

    public string File { get; }

    /// <summary>A record was added or the statistics were reset.</summary>
    public event Action? Changed;

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString);
        db.Open();
        Execute(db, "PRAGMA busy_timeout = 3000");
        return db;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int DayKey(DateTime local) => local.Year * 10000 + local.Month * 100 + local.Day;
    private static DateOnly FromDayKey(long key) => new((int)(key / 10000), (int)(key / 100 % 100), (int)(key % 100));

    public void Add(TransferRecord record)
    {
        var local = record.Finished.ToLocalTime().DateTime;
        using (var db = Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO transfers (finished, day, hour, kind, result, source_site, target_site, file_name, bytes, duration_ms)
                VALUES ($finished, $day, $hour, $kind, $result, $source, $target, $name, $bytes, $ms)
                """;
            cmd.Parameters.AddWithValue("$finished", record.Finished.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$day", DayKey(local));
            cmd.Parameters.AddWithValue("$hour", local.Hour);
            cmd.Parameters.AddWithValue("$kind", (int)record.Kind);
            cmd.Parameters.AddWithValue("$result", (int)record.Result);
            cmd.Parameters.AddWithValue("$source", record.SourceSite);
            cmd.Parameters.AddWithValue("$target", record.TargetSite);
            cmd.Parameters.AddWithValue("$name", record.FileName);
            cmd.Parameters.AddWithValue("$bytes", Math.Max(0, record.Bytes));
            cmd.Parameters.AddWithValue("$ms", (long)Math.Max(0, record.Duration.TotalMilliseconds));
            cmd.ExecuteNonQuery();
        }
        Changed?.Invoke();
    }

    /// <summary>Deletes all statistics.</summary>
    public void Clear()
    {
        using (var db = Open())
        {
            Execute(db, "DELETE FROM transfers");
            Execute(db, "VACUUM");
        }
        Changed?.Invoke();
    }

    /// <summary>Everything the statistics view shows, relative to <paramref name="now"/> (local time).</summary>
    public StatsSnapshot Snapshot(DateTime now, int days = 30)
    {
        var today = now.Date;
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var yearStart = new DateTime(today.Year, 1, 1);
        var firstDay = today.AddDays(1 - days);

        using var db = Open();
        return new StatsSnapshot(
            Totals(db, DayKey(today)),
            Totals(db, DayKey(weekStart)),
            Totals(db, DayKey(monthStart)),
            Totals(db, DayKey(yearStart)),
            Totals(db, 0),
            LastDays(db, firstDay, days),
            TopSites(db, 5),
            SingleRecord(db, "result = 0 ORDER BY bytes DESC"),
            // At least 1 MiB and half a second, otherwise tiny files produce meaningless peaks.
            SingleRecord(db, "result = 0 AND bytes >= 1048576 AND duration_ms >= 500 ORDER BY bytes * 1.0 / duration_ms DESC"),
            BusiestDay(db),
            Scalar(db, "SELECT hour FROM transfers GROUP BY hour HAVING SUM(bytes) > 0 ORDER BY SUM(bytes) DESC LIMIT 1") is long h ? (int)h : null,
            Scalar(db, "SELECT MIN(finished) FROM transfers") is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime() : null,
            Convert.ToInt32(Scalar(db, "SELECT COUNT(*) FROM transfers WHERE result = 2"), CultureInfo.InvariantCulture));
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private static PeriodTotals Totals(SqliteConnection db, int fromDay)
    {
        using var cmd = db.CreateCommand();
        // Speed only over attempts that moved data, so failed FXP attempts (0 bytes) do not drag it down.
        cmd.CommandText = """
            SELECT kind,
                   SUM(bytes),
                   SUM(result = 0),
                   SUM(CASE WHEN bytes > 0 THEN duration_ms ELSE 0 END),
                   SUM(result = 1)
            FROM transfers WHERE day >= $from GROUP BY kind
            """;
        cmd.Parameters.AddWithValue("$from", fromDay);
        var totals = new Dictionary<TransferKind, KindTotals>();
        var failed = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            totals[(TransferKind)reader.GetInt32(0)] = new KindTotals(reader.GetInt64(1), reader.GetInt32(2), reader.GetInt64(3) / 1000.0);
            failed += reader.GetInt32(4);
        }
        return new PeriodTotals(
            totals.GetValueOrDefault(TransferKind.Download, KindTotals.Empty),
            totals.GetValueOrDefault(TransferKind.Upload, KindTotals.Empty),
            totals.GetValueOrDefault(TransferKind.Fxp, KindTotals.Empty),
            failed);
    }

    private static List<DayTotals> LastDays(SqliteConnection db, DateTime firstDay, int days)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT day, kind, SUM(bytes) FROM transfers WHERE day >= $from GROUP BY day, kind";
        cmd.Parameters.AddWithValue("$from", DayKey(firstDay));
        var map = new Dictionary<DateOnly, (long D, long U, long F)>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var day = FromDayKey(reader.GetInt64(0));
                var bytes = reader.GetInt64(2);
                var entry = map.GetValueOrDefault(day);
                map[day] = (TransferKind)reader.GetInt32(1) switch
                {
                    TransferKind.Download => entry with { D = entry.D + bytes },
                    TransferKind.Upload => entry with { U = entry.U + bytes },
                    _ => entry with { F = entry.F + bytes },
                };
            }
        }
        return Enumerable.Range(0, days).Select(i =>
        {
            var day = DateOnly.FromDateTime(firstDay.AddDays(i));
            var (d, u, f) = map.GetValueOrDefault(day);
            return new DayTotals(day, d, u, f);
        }).ToList();
    }

    /// <summary>Remote sites by volume: the source of downloads, the target of uploads, both sides of FXP.</summary>
    private static List<SiteTotals> TopSites(SqliteConnection db, int limit)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT site, SUM(bytes), SUM(result = 0) FROM (
                SELECT source_site AS site, bytes, result FROM transfers WHERE kind IN ({(int)TransferKind.Download}, {(int)TransferKind.Fxp})
                UNION ALL
                SELECT target_site AS site, bytes, result FROM transfers WHERE kind IN ({(int)TransferKind.Upload}, {(int)TransferKind.Fxp})
            ) GROUP BY site HAVING SUM(bytes) > 0 ORDER BY SUM(bytes) DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<SiteTotals>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add(new SiteTotals(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)));
        return list;
    }

    private static TransferRecord? SingleRecord(SqliteConnection db, string whereOrder)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT finished, kind, result, source_site, target_site, file_name, bytes, duration_ms FROM transfers WHERE " + whereOrder + " LIMIT 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new TransferRecord(DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0)).ToLocalTime(),
            (TransferKind)reader.GetInt32(1), (TransferResult)reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetInt64(6), TimeSpan.FromMilliseconds(reader.GetInt64(7)));
    }

    private static DayTotals? BusiestDay(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"""
            SELECT day,
                   SUM(CASE WHEN kind = {(int)TransferKind.Download} THEN bytes ELSE 0 END),
                   SUM(CASE WHEN kind = {(int)TransferKind.Upload} THEN bytes ELSE 0 END),
                   SUM(CASE WHEN kind = {(int)TransferKind.Fxp} THEN bytes ELSE 0 END)
            FROM transfers GROUP BY day HAVING SUM(bytes) > 0 ORDER BY SUM(bytes) DESC LIMIT 1
            """;
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new DayTotals(FromDayKey(reader.GetInt64(0)), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)) : null;
    }

    /// <summary>Closes pooled connections so the database file can be copied or moved.</summary>
    public static void ReleaseFiles() => SqliteConnection.ClearAllPools();
}
