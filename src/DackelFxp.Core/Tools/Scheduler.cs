using DackelFxp.Core.Settings;

namespace DackelFxp.Core.Tools;

/// <summary>Fires <see cref="ScheduleEntry"/> actions at their configured times.</summary>
public sealed class Scheduler : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly Func<DateTime> _now;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public Scheduler(SettingsStore settings, Func<DateTime>? now = null)
    {
        _settings = settings;
        _now = now ?? (() => DateTime.Now);
    }

    public event Action<ScheduleEntry>? Triggered;

    public void Start() => _loop ??= LoopAsync(_cts.Token);

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) Tick();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Checks all entries once (public for tests).</summary>
    public void Tick()
    {
        var now = _now();
        var changed = false;
        foreach (var entry in _settings.Current.Schedules.ToList())
        {
            if (!entry.Enabled || !IsDue(entry, now)) continue;
            entry.LastRun = now;
            if (entry.Days.Count == 0) entry.Enabled = false;
            changed = true;
            Triggered?.Invoke(entry);
        }
        if (changed) _settings.Save();
    }

    public static bool IsDue(ScheduleEntry entry, DateTime now)
    {
        var dueToday = now.Date + entry.Time.ToTimeSpan();
        if (now < dueToday || now - dueToday > TimeSpan.FromMinutes(5)) return false;
        if (entry.LastRun is { } last && last.LocalDateTime >= dueToday) return false;
        if (entry.Days.Count > 0) return entry.Days.Contains(now.DayOfWeek);
        return entry.Date is null || entry.Date == DateOnly.FromDateTime(now);
    }

    public static DateTime? NextRun(ScheduleEntry entry, DateTime now)
    {
        if (!entry.Enabled) return null;
        for (var i = 0; i < 8; i++)
        {
            var day = now.Date.AddDays(i);
            var at = day + entry.Time.ToTimeSpan();
            if (at < now) continue;
            if (entry.Days.Count > 0 ? entry.Days.Contains(day.DayOfWeek) : entry.Date is null || entry.Date == DateOnly.FromDateTime(day))
                return at;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null) await _loop.ConfigureAwait(false);
    }
}
