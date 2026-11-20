using DackelFxp.Core.Queue;
using DackelFxp.Core.Stats;
using Microsoft.Data.Sqlite;

namespace DackelFxp.Core.Tests;

public sealed class StatsStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dackel-stats").FullName;
    private readonly StatsStore _store;

    public StatsStoreTests() => _store = new StatsStore(Path.Combine(_root, "stats.db"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }

    private static readonly DateTime Now = new(2026, 11, 20, 15, 30, 0); // a Friday

    private void Add(DateTime local, TransferKind kind, long bytes, double seconds = 1, TransferResult result = TransferResult.Ok,
        string source = "Lokal", string target = "Lokal", string name = "f.bin") =>
        _store.Add(new TransferRecord(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)), kind, result, source, target, name, bytes, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Sums_Per_Period_And_Direction()
    {
        Add(Now.AddHours(-1), TransferKind.Download, 1000, 2, source: "A");             // today
        Add(Now.AddHours(-2), TransferKind.Upload, 300, 3, target: "B");                // today
        Add(new DateTime(2026, 11, 16, 10, 0, 0), TransferKind.Download, 500, source: "A"); // Monday, this week
        Add(new DateTime(2026, 11, 15, 10, 0, 0), TransferKind.Fxp, 700, source: "A", target: "B"); // Sunday, last week
        Add(new DateTime(2026, 11, 1, 0, 5, 0), TransferKind.Download, 50, source: "C");    // this month
        Add(new DateTime(2026, 2, 1, 12, 0, 0), TransferKind.Upload, 20, target: "B");      // this year
        Add(new DateTime(2025, 12, 31, 23, 0, 0), TransferKind.Download, 9, source: "A");   // last year
        Add(Now.AddMinutes(-5), TransferKind.Download, 100, 1, TransferResult.Failed, source: "A");

        var s = _store.Snapshot(Now);

        Assert.Equal(1100, s.Today.Download.Bytes);
        Assert.Equal(1, s.Today.Download.Files);
        Assert.Equal(1, s.Today.Failed);
        Assert.Equal(300, s.Today.Upload.Bytes);
        Assert.Equal(100, s.Today.Upload.AverageSpeed);
        Assert.Equal(1100 / 3.0, s.Today.Download.AverageSpeed, 3);
        Assert.Equal(1600, s.Week.Download.Bytes);
        Assert.Equal(0, s.Week.Fxp.Bytes);
        Assert.Equal(700, s.Month.Fxp.Bytes);
        Assert.Equal(1650, s.Month.Download.Bytes);
        Assert.Equal(320, s.Year.Upload.Bytes);
        Assert.Equal(1659, s.All.Download.Bytes);
        Assert.Equal(7, s.All.Files);

        Assert.Equal(30, s.LastDays.Count);
        Assert.Equal(DateOnly.FromDateTime(Now), s.LastDays[^1].Day);
        Assert.Equal(1100, s.LastDays[^1].Download);
        Assert.Equal(700, s.LastDays.Single(d => d.Day == new DateOnly(2026, 11, 15)).Fxp);

        // A: 1100 + 500 + 700 (FXP source) + 9; B: 300 + 700 (FXP target) + 20.
        Assert.Equal(["A", "B", "C"], s.TopSites.Select(t => t.Site));
        Assert.Equal(2309, s.TopSites[0].Bytes);
        Assert.Equal(new DateOnly(2026, 11, 20), s.BusiestDay?.Day);
        Assert.Equal(10, s.BusiestHour); // 500 + 700 bytes beat 1000 + 100 at 14:00
        Assert.Equal(1000, s.Largest?.Bytes);
        Assert.Equal(new DateTime(2025, 12, 31, 23, 0, 0), s.Since?.DateTime);
    }

    [Fact]
    public void Fastest_Ignores_Tiny_Transfers_And_Clear_Empties()
    {
        Add(Now, TransferKind.Download, 10, 0.001, name: "tiny");
        Add(Now, TransferKind.Download, 10 << 20, 10, name: "slow");
        Add(Now, TransferKind.Upload, 10 << 20, 2, name: "fast");
        var changed = 0;
        _store.Changed += () => changed++;

        Assert.Equal("fast", _store.Snapshot(Now).Fastest?.FileName);

        _store.Clear();
        var s = _store.Snapshot(Now);
        Assert.Equal(1, changed);
        Assert.Equal(0, s.All.Bytes);
        Assert.Null(s.Since);
        Assert.Null(s.Fastest);
        Assert.Empty(s.TopSites);
    }

    [Fact]
    public void Data_Survives_Reopening()
    {
        Add(Now, TransferKind.Download, 42);
        var reopened = new StatsStore(_store.File);
        Assert.Equal(42, reopened.Snapshot(Now).All.Download.Bytes);
    }
}
