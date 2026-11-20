using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DackelFxp.App.Converters;
using DackelFxp.App.Services;
using DackelFxp.Core.Stats;
using DackelFxp.Ftp;

namespace DackelFxp.App.ViewModels;

/// <summary>One line of the period table; the first line holds the column headers.</summary>
public sealed record StatsRow(string Period, string Download, string Upload, string Fxp, string Files, string Failed,
    string AvgDownload, string AvgUpload, string AvgFxp, bool IsHeader = false);

/// <summary>One day in the bar chart; heights in pixels, stacked download / upload / FXP.</summary>
public sealed record DayBar(string Label, string Tip, double DownloadHeight, double UploadHeight, double FxpHeight, bool IsToday)
{
    public bool HasDownload => DownloadHeight > 0;
    public bool HasUpload => UploadHeight > 0;
    public bool HasFxp => FxpHeight > 0;
}

public sealed record StatsFact(string Label, string Value);

public sealed partial class StatisticsViewModel : ObservableObject
{
    public const double ChartHeight = 220;
    private readonly AppServices _services;
    private readonly DispatcherTimer _debounce;
    private bool _refreshing;
    private bool _again;

    public StatisticsViewModel(AppServices services)
    {
        _services = services;
        // Transfers can finish many times per second: refresh at most once per second.
        _debounce = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            _ = RefreshAsync();
        });
        if (services.Stats is { } stats) stats.Changed += () => Dispatcher.UIThread.Post(() => { if (!_debounce.IsEnabled) _debounce.Start(); });
        // "Today" moves on at midnight even without transfers.
        new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) =>
        {
            if (_lastDay != DateTime.Today) _ = RefreshAsync();
        }).Start();
        _ = RefreshAsync();
    }

    private DateTime _lastDay;

    public bool IsAvailable => _services.Stats is not null;

    [ObservableProperty] public partial IReadOnlyList<StatsRow> Rows { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<DayBar> Days { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<StatsFact> Facts { get; private set; } = [];
    [ObservableProperty] public partial string ChartTitle { get; private set; } = "";
    [ObservableProperty] public partial string ChartMax { get; private set; } = "";

    public void RefreshTexts() => _ = RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_services.Stats is not { } stats)
        {
            Facts = [new StatsFact(Lang.T("Statistik", "Statistics"), Lang.T("Datenbank nicht verfügbar (siehe Log)", "Database not available (see log)"))];
            return;
        }
        if (_refreshing)
        {
            _again = true;
            return;
        }
        _refreshing = true;
        try
        {
            do
            {
                _again = false;
                var now = DateTime.Now;
                StatsSnapshot snapshot;
                try { snapshot = await Task.Run(() => stats.Snapshot(now)); }
                catch (Microsoft.Data.Sqlite.SqliteException ex)
                {
                    _services.Log.AddError(Lang.T("Statistik", "Statistics"), ex.Message);
                    return;
                }
                _lastDay = now.Date;
                Apply(snapshot, now);
            } while (_again);
        }
        finally
        {
            _refreshing = false;
        }
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (_services.Stats is not { } stats) return;
        if (!await _services.Dialogs.ConfirmAsync(Lang.T("Statistik zurücksetzen", "Reset statistics"),
                Lang.T("Alle gesammelten Statistiken löschen?", "Delete all collected statistics?"))) return;
        await Task.Run(stats.Clear);
        await RefreshAsync();
    }

    private static string Amount(long bytes) => bytes > 0 ? Formats.Bytes(bytes) : "–";
    private static string Speed(double speed) => speed > 0 ? Formats.Speed(speed) : "–";
    private static string Count(int n) => n > 0 ? n.ToString("N0", CultureInfo.CurrentCulture) : "–";
    private static CultureInfo Culture => Lang.German ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.GetCultureInfo("en-GB");

    private static StatsRow Row(string period, PeriodTotals t) => new(period,
        Amount(t.Download.Bytes), Amount(t.Upload.Bytes), Amount(t.Fxp.Bytes), Count(t.Files), Count(t.Failed),
        Speed(t.Download.AverageSpeed), Speed(t.Upload.AverageSpeed), Speed(t.Fxp.AverageSpeed));

    private void Apply(StatsSnapshot s, DateTime now)
    {
        Rows =
        [
            new StatsRow(Lang.T("Zeitraum", "Period"), "⬇ Download", "⬆ Upload", "⇄ FXP", Lang.T("Dateien", "Files"), Lang.T("Fehler", "Failed"),
                "Ø ⬇", "Ø ⬆", "Ø FXP", IsHeader: true),
            Row(Lang.T("Heute", "Today"), s.Today),
            Row(Lang.T("Woche", "Week"), s.Week),
            Row(Lang.T("Monat", "Month"), s.Month),
            Row(Lang.T("Jahr", "Year"), s.Year),
            Row(Lang.T("Gesamt", "All time"), s.All),
        ];

        var max = Math.Max(1, s.LastDays.Max(d => d.Total));
        double Px(long bytes) => bytes <= 0 ? 0 : Math.Max(2, bytes * ChartHeight / max);
        var culture = Culture;
        var today = DateOnly.FromDateTime(now);
        Days = s.LastDays.Select((d, i) => new DayBar(
            // Label every 2nd day counted back from today, so today always has one.
            (s.LastDays.Count - 1 - i) % 2 == 0 ? d.Day.Day.ToString(culture) : "",
            d.Day.ToString("ddd, d. MMM yyyy", culture) + "\n" +
            $"⬇ {Formats.Bytes(d.Download)}\n⬆ {Formats.Bytes(d.Upload)}\n⇄ {Formats.Bytes(d.Fxp)}",
            Px(d.Download), Px(d.Upload), Px(d.Fxp), d.Day == today)).ToList();
        ChartTitle = Lang.T($"Letzte {s.LastDays.Count} Tage", $"Last {s.LastDays.Count} days");
        ChartMax = s.LastDays.Any(d => d.Total > 0) ? Formats.Bytes(max) : "";

        Facts = BuildFacts(s, now, culture);
    }

    private static string Files(int n) => n == 1 ? Lang.T("1 Datei", "1 file") : Lang.T($"{n:N0} Dateien", $"{n:N0} files");

    private static List<StatsFact> BuildFacts(StatsSnapshot s, DateTime now, CultureInfo culture)
    {
        var all = s.All;
        var facts = new List<StatsFact>
        {
            new(Lang.T("Gesamt übertragen", "Total transferred"),
                Lang.T($"{Formats.Bytes(all.Bytes)} in {Files(all.Files)}", $"{Formats.Bytes(all.Bytes)} in {Files(all.Files)}")),
        };
        if (s.Since is not { } since) return facts;

        var attempts = all.Files + all.Failed;
        if (attempts > 0)
            facts.Add(new(Lang.T("Erfolgsquote", "Success rate"),
                (all.Files * 100.0 / attempts).ToString("0.0", culture) + " %" +
                Lang.T($" ({all.Failed:N0} Fehler, {s.Aborted:N0} abgebrochen)", $" ({all.Failed:N0} failed, {s.Aborted:N0} aborted)")));

        if (all.Download.Bytes > 0 && all.Upload.Bytes > 0)
            facts.Add(new(Lang.T("Verhältnis ⬆ : ⬇", "Ratio ⬆ : ⬇"), "1 : " + (all.Download.Bytes / (double)all.Upload.Bytes).ToString("0.00", culture)));

        var days = Math.Max(1, (now.Date - since.Date).Days + 1);
        facts.Add(new(Lang.T("Ø pro Tag", "Avg. per day"), Formats.Bytes(all.Bytes / days)));

        if (s.Largest is { } largest)
            facts.Add(new(Lang.T("Größte Datei", "Largest file"), $"{Formats.Bytes(largest.Bytes)} – {largest.FileName}"));
        if (s.Fastest is { } fastest)
            facts.Add(new(Lang.T("Schnellste Übertragung", "Fastest transfer"), $"{Formats.Speed(fastest.Speed)} – {fastest.FileName}"));
        if (s.BusiestDay is { } busiest)
            facts.Add(new(Lang.T("Stärkster Tag", "Busiest day"), $"{busiest.Day.ToString("d", culture)} – {Formats.Bytes(busiest.Total)}"));
        if (s.BusiestHour is { } hour)
            facts.Add(new(Lang.T("Aktivste Stunde", "Busiest hour"), $"{hour:00}:00–{(hour + 1) % 24:00}:00"));

        for (var i = 0; i < s.TopSites.Count; i++)
        {
            var site = s.TopSites[i];
            facts.Add(new(i == 0 ? Lang.T("Top-Sites", "Top sites") : "",
                $"{i + 1}. {site.Site} – {Formats.Bytes(site.Bytes)} ({Files(site.Files)})"));
        }

        facts.Add(new(Lang.T("Erfasst seit", "Recorded since"), since.ToString("d", culture)));
        return facts;
    }
}
