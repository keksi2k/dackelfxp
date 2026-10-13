using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DackelFxp.App.Converters;
using DackelFxp.App.Services;
using DackelFxp.Core.Browsing;
using DackelFxp.Core.Settings;
using DackelFxp.Core.Sites;
using DackelFxp.Core.Tools;
using DackelFxp.Ftp.Protocol;

namespace DackelFxp.App.ViewModels;

/// <summary>One side of the dual-pane browser.</summary>
public sealed partial class BrowserPaneViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Stack<string> _back = new();
    private List<FileEntryViewModel> _all = [];
    private CancellationTokenSource? _listCts;

    public BrowserPaneViewModel(AppServices services, string name)
    {
        _services = services;
        Name = name;
        Browser = new LocalBrowser();
    }

    public string Name { get; }
    public BrowserPaneViewModel? Other { get; set; }

    /// <summary>Set by the main view model: transfer the given entries to the other pane.</summary>
    public Func<BrowserPaneViewModel, IReadOnlyList<FileEntryViewModel>, bool, Task>? TransferHandler { get; set; }

    /// <summary>Set by the main view model: upload local paths dropped from the OS file manager.</summary>
    public Func<BrowserPaneViewModel, IReadOnlyList<string>, Task>? ExternalDropHandler { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsRemote), nameof(Site), nameof(Bookmarks))]
    public partial IBrowser Browser { get; private set; }

    public string Title => Browser.Title;
    public bool IsRemote => Browser.Site is not null;
    public Site? Site => Browser.Site;
    public IReadOnlyList<Bookmark> Bookmarks => Site?.Bookmarks ?? [];

    [ObservableProperty] public partial string CurrentPath { get; private set; } = "";
    [ObservableProperty] public partial string PathText { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string StatusText { get; private set; } = "";
    [ObservableProperty] public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value) => ApplyFilter();

    public ObservableCollection<FileEntryViewModel> Items { get; } = [];
    public IReadOnlyList<FileEntryViewModel> AllEntries => _all;
    public IList<FileEntryViewModel> Selection { get; set; } = [];

    public IReadOnlyList<Site> Sites => _services.Sites.Sites.OrderBy(s => s.Group).ThenBy(s => s.Name).ToList();
    public IReadOnlyList<CustomCommand> CustomCommands =>
        _services.Settings.Current.CustomCommands.Where(c => c.SiteId is null || c.SiteId == Site?.Id).ToList();

    public void NotifySitesChanged()
    {
        OnPropertyChanged(nameof(Sites));
        OnPropertyChanged(nameof(CustomCommands));
        OnPropertyChanged(nameof(Bookmarks));
    }

    // ---------------------------------------------------------------- navigation

    public async Task InitializeLocalAsync(string? path)
    {
        Browser = new LocalBrowser();
        var start = !string.IsNullOrEmpty(path) && Directory.Exists(path) ? path : await Browser.GetHomeAsync(default);
        await LoadAsync(start, pushHistory: false);
    }

    [RelayCommand]
    public async Task ConnectAsync(Site site)
    {
        Browser = new RemoteBrowser(_services.Connections, site);
        _back.Clear();
        _all = [];
        Items.Clear();
        CurrentPath = "";
        PathText = "";
        await RunAsync($"Verbinde mit {site.DisplayName}...", async ct =>
        {
            var home = await Browser.GetHomeAsync(ct);
            await LoadCoreAsync(home, ct);
        });
        if (Other is { IsRemote: false } other && !string.IsNullOrWhiteSpace(site.LocalPath) && Directory.Exists(site.LocalPath))
            await other.NavigateAsync(site.LocalPath);
        NotifySitesChanged();
    }

    [RelayCommand]
    private async Task ConnectLocalAsync()
    {
        if (Browser is RemoteBrowser remote) await remote.DisconnectAsync();
        _back.Clear();
        await InitializeLocalAsync(_services.Settings.Current.LastLocalPath);
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (Browser is not RemoteBrowser remote) return;
        await remote.DisconnectAsync();
        _services.Log.AddInfo(remote.Title, "Getrennt");
        await ConnectLocalAsync();
    }

    public Task NavigateAsync(string path) => LoadAsync(path, pushHistory: true);

    [RelayCommand]
    private Task NavigateToPathTextAsync() => NavigateAsync(PathText.Trim());

    [RelayCommand]
    public Task RefreshAsync() => LoadAsync(CurrentPath, pushHistory: false);

    [RelayCommand]
    private Task UpAsync() => NavigateAsync(Browser.GetParent(CurrentPath));

    [RelayCommand]
    private async Task HomeAsync()
    {
        var home = await Browser.GetHomeAsync(default);
        await NavigateAsync(home);
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        if (_back.Count == 0) return;
        await LoadAsync(_back.Pop(), pushHistory: false);
    }

    [RelayCommand]
    public async Task OpenAsync(FileEntryViewModel? entry)
    {
        if (entry is null) return;
        if (entry.IsDirectory)
        {
            await NavigateAsync(entry.IsDrive ? entry.Name : Browser.Combine(CurrentPath, entry.Name));
            return;
        }
        // Double-click on a file transfers it, like FlashFXP.
        await TransferAsync([entry], startNow: true);
    }

    [RelayCommand]
    private Task GoToBookmarkAsync(Bookmark bookmark) => NavigateAsync(bookmark.RemotePath);

    [RelayCommand]
    private async Task AddBookmarkAsync()
    {
        if (Site is not { } site) return;
        var name = await _services.Dialogs.PromptAsync("Lesezeichen", "Name des Lesezeichens:", FtpPath.GetName(CurrentPath));
        if (string.IsNullOrWhiteSpace(name)) return;
        site.Bookmarks.Add(new Bookmark { Name = name, RemotePath = CurrentPath, LocalPath = Other is { IsRemote: false } o ? o.CurrentPath : null });
        if (!site.IsTemporary) _services.Sites.AddOrUpdate(site);
        OnPropertyChanged(nameof(Bookmarks));
    }

    private Task LoadAsync(string path, bool pushHistory) => RunAsync(null, async ct =>
    {
        var previous = CurrentPath;
        await LoadCoreAsync(path, ct);
        if (pushHistory && previous != CurrentPath && previous.Length > 0) _back.Push(previous);
    });

    private async Task LoadCoreAsync(string path, CancellationToken ct)
    {
        var (actual, entries) = await Browser.ListAsync(path, ct);
        var isDriveList = !IsRemote && actual.Length == 0;
        _all = entries.Select(e => new FileEntryViewModel(e, isDriveList)).ToList();
        CurrentPath = actual;
        PathText = actual.Length == 0 ? "Laufwerke" : actual;
        if (!IsRemote && actual.Length > 0) _services.Settings.Current.LastLocalPath = actual;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        Items.Clear();
        var filter = Filter.Trim();
        var rule = filter.Length == 0 ? null : new Core.Rules.SkipRule { Pattern = filter.Contains('*') || filter.Contains('?') ? filter : $"*{filter}*" };
        foreach (var item in _all
                     .Where(e => rule is null || e.IsDirectory || rule.Matches(e.Name, false))
                     .OrderByDescending(e => e.IsDirectory)
                     .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            Items.Add(item);
        var files = _all.Count(e => !e.IsDirectory);
        var dirs = _all.Count - files;
        StatusText = $"{dirs} Ordner, {files} Dateien, {Formats.Bytes(_all.Where(e => !e.IsDirectory).Sum(e => e.Entry.Size))}";
    }

    // ---------------------------------------------------------------- file operations

    [RelayCommand]
    private async Task MakeDirectoryAsync()
    {
        var name = await _services.Dialogs.PromptAsync("Neuer Ordner", "Name des neuen Ordners:");
        if (string.IsNullOrWhiteSpace(name)) return;
        await RunAsync(null, async ct =>
        {
            await Browser.CreateDirectoryAsync(Browser.Combine(CurrentPath, name.Trim()), ct);
            await LoadCoreAsync(CurrentPath, ct);
        });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var selected = Selection.Where(e => !e.IsDrive).ToList();
        if (selected.Count == 0) return;
        var what = selected.Count == 1 ? $"'{selected[0].Name}'" : $"{selected.Count} Einträge";
        if (!await _services.Dialogs.ConfirmAsync("Löschen", $"{what} wirklich löschen? Ordner werden mit Inhalt gelöscht.")) return;
        await RunAsync($"Lösche {what}...", async ct =>
        {
            foreach (var entry in selected)
                await Browser.DeleteAsync(Browser.Combine(CurrentPath, entry.Name), entry.IsDirectory, ct);
            await LoadCoreAsync(CurrentPath, ct);
        });
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (Selection.FirstOrDefault() is not { IsDrive: false } entry) return;
        var name = await _services.Dialogs.PromptAsync("Umbenennen", $"Neuer Name für '{entry.Name}':", entry.Name);
        if (string.IsNullOrWhiteSpace(name) || name == entry.Name) return;
        await RunAsync(null, async ct =>
        {
            await Browser.RenameAsync(Browser.Combine(CurrentPath, entry.Name), Browser.Combine(CurrentPath, name.Trim()), ct);
            await LoadCoreAsync(CurrentPath, ct);
        });
    }

    [RelayCommand]
    private async Task ChmodAsync()
    {
        if (Browser is not RemoteBrowser remote || Selection.Count == 0) return;
        var current = Selection[0].Permissions;
        var mode = await _services.Dialogs.PromptAsync("CHMOD", $"Rechte (oktal) für {Selection.Count} Einträge, aktuell {current}:", PermissionsToOctal(current));
        if (string.IsNullOrWhiteSpace(mode)) return;
        var targets = Selection.ToList();
        await RunAsync(null, async ct =>
        {
            foreach (var entry in targets)
                await remote.SetPermissionsAsync(Browser.Combine(CurrentPath, entry.Name), mode.Trim(), ct);
            await LoadCoreAsync(CurrentPath, ct);
        });
    }

    internal static string PermissionsToOctal(string perms)
    {
        if (perms.Length < 10) return "755";
        int Digit(int o) => (perms[o] != '-' ? 4 : 0) + (perms[o + 1] != '-' ? 2 : 0) + (perms[o + 2] is not '-' and not 'S' and not 'T' ? 1 : 0);
        return $"{Digit(1)}{Digit(4)}{Digit(7)}";
    }

    [RelayCommand]
    private async Task RawCommandAsync()
    {
        if (Browser is not RemoteBrowser remote) return;
        var command = await _services.Dialogs.PromptAsync("Befehl senden", "FTP-Befehl (z. B. SITE WHO):");
        if (string.IsNullOrWhiteSpace(command)) return;
        await RunCommandsAsync(remote, [command.Trim()], command.Trim());
    }

    [RelayCommand]
    private async Task RunCustomCommandAsync(CustomCommand command)
    {
        if (Browser is not RemoteBrowser remote) return;
        string? parameter = null;
        if (command.AskForParameter)
        {
            parameter = await _services.Dialogs.PromptAsync(command.Name, "Parameter (%1):");
            if (parameter is null) return;
        }
        var file = Selection.FirstOrDefault()?.Name;
        var lines = command.Expand(CurrentPath, file, remote.Title, parameter).ToList();
        await RunCommandsAsync(remote, lines, command.Name);
    }

    private Task RunCommandsAsync(RemoteBrowser remote, IReadOnlyList<string> commands, string title) =>
        RunAsync(null, async ct =>
        {
            var output = await remote.ExecuteAsync(commands, ct);
            _services.Dialogs.ShowText($"{title} – {remote.Title}", string.Join(Environment.NewLine, output));
        });

    [RelayCommand]
    private Task TransferSelectedAsync() => TransferAsync(Selection.ToList(), startNow: true);

    [RelayCommand]
    private Task QueueSelectedAsync() => TransferAsync(Selection.ToList(), startNow: false);

    public Task TransferAsync(IReadOnlyList<FileEntryViewModel> entries, bool startNow) =>
        entries.Count == 0 || TransferHandler is null ? Task.CompletedTask : TransferHandler(this, entries, startNow);

    /// <summary>Selects entries that differ from the other pane (after compare).</summary>
    public IReadOnlyList<FileEntryViewModel> DifferentEntries() =>
        Items.Where(e => e.Compare is CompareStatus.OnlyLeft or CompareStatus.OnlyRight or CompareStatus.Different or CompareStatus.Newer).ToList();

    [RelayCommand]
    private async Task ViewEditAsync()
    {
        if (Selection.FirstOrDefault() is not { IsDirectory: false } entry) return;
        var path = Browser.Combine(CurrentPath, entry.Name);
        if (Browser is not RemoteBrowser remote)
        {
            OpenWithShell(path);
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "dackelfxp", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var local = Path.Combine(tempDir, entry.Name);
        var site = remote.Site;
        await RunAsync($"Lade {entry.Name}...", async ct =>
        {
            await using var conn = await _services.Connections.RentAsync(site, ct);
            await using var fs = File.Create(local);
            await conn.Client.DownloadAsync(path, fs, ct: ct);
        });
        if (!File.Exists(local)) return;
        OpenWithShell(local);
        WatchForUpload(site, local, path);
    }

    private void WatchForUpload(Site site, string local, string remotePath)
    {
        var watcher = new FileSystemWatcher(Path.GetDirectoryName(local)!, Path.GetFileName(local))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        var pending = 0;
        watcher.Changed += (_, _) =>
        {
            if (Interlocked.Exchange(ref pending, 1) == 1) return;
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(500);
                try
                {
                    if (!await _services.Dialogs.ConfirmAsync("Datei geändert", $"'{Path.GetFileName(local)}' wurde geändert. Auf {site.DisplayName} hochladen?"))
                        return;
                    await RunAsync($"Lade {Path.GetFileName(local)} hoch...", async ct =>
                    {
                        await using var conn = await _services.Connections.RentAsync(site, ct);
                        await using var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        await conn.Client.UploadAsync(fs, remotePath, ct: ct);
                        if (IsRemote && Site?.Id == site.Id) await LoadCoreAsync(CurrentPath, ct);
                    });
                }
                finally
                {
                    Volatile.Write(ref pending, 0);
                }
            });
        };
        _watchers.Add(watcher);
    }

    private readonly List<FileSystemWatcher> _watchers = [];

    private void OpenWithShell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _services.Log.AddError("Lokal", $"Kann '{path}' nicht öffnen: {ex.Message}");
        }
    }

    [RelayCommand]
    private void VerifySfv()
    {
        if (IsRemote) return;
        var sfv = Selection.FirstOrDefault(e => e.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase))
                  ?? _all.FirstOrDefault(e => e.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase));
        if (sfv is null)
        {
            _ = _services.Dialogs.ShowMessageAsync("SFV prüfen", "Keine .sfv-Datei in diesem Ordner.");
            return;
        }
        _ = RunAsync("Prüfe SFV...", async ct =>
        {
            var results = await Task.Run(() => SfvVerifier.Verify(Browser.Combine(CurrentPath, sfv.Name)), ct);
            var lines = results.Select(r => $"{r.Status,-8} {r.FileName}" + (r.Actual is { } a && r.Status == SfvStatus.Bad ? $"  (erwartet {r.Expected:X8}, ist {a:X8})" : ""));
            var summary = $"{results.Count(r => r.Status == SfvStatus.Ok)} OK, {results.Count(r => r.Status == SfvStatus.Bad)} fehlerhaft, {results.Count(r => r.Status == SfvStatus.Missing)} fehlen";
            _services.Dialogs.ShowText($"SFV: {sfv.Name}", summary + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines));
        });
    }

    // ---------------------------------------------------------------- helpers

    private async Task RunAsync(string? busyText, Func<CancellationToken, Task> action)
    {
        _listCts?.Cancel();
        var cts = new CancellationTokenSource();
        _listCts = cts;
        IsBusy = true;
        if (busyText is not null) StatusText = busyText;
        try
        {
            await action(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is FtpException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Security.SecurityException or ArgumentException)
        {
            StatusText = "Fehler: " + ex.Message;
            _services.Log.AddError(Title, ex.Message);
        }
        finally
        {
            if (_listCts == cts) IsBusy = false;
        }
    }
}
