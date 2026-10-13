using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DackelFxp.App.Services;
using DackelFxp.App.Views.Dialogs;
using DackelFxp.Core.Queue;
using DackelFxp.Core.Settings;
using DackelFxp.Core.Sites;
using DackelFxp.Core.Tools;
using DackelFxp.Ftp;

namespace DackelFxp.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly AppServices _services;

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        Left = new BrowserPaneViewModel(services, isLeft: true) { TransferHandler = TransferAsync, ExternalDropHandler = UploadDroppedAsync };
        Right = new BrowserPaneViewModel(services, isLeft: false) { TransferHandler = TransferAsync, ExternalDropHandler = UploadDroppedAsync };
        Left.Other = Right;
        Right.Other = Left;
        Left.IsActive = true;
        Left.ShowLog = Right.ShowLog = () => BottomTabIndex = 1;
        Queue = new QueueViewModel(services);
        Log = new LogViewModel(services);
        CommandBar = new CommandBarViewModel(services, Left, Right);

        Lang.Changed += () => Dispatcher.UIThread.Post(OnLanguageChanged);

        services.Sites.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            Left.NotifySitesChanged();
            Right.NotifySitesChanged();
            OnPropertyChanged(nameof(Sites));
        });
        services.Engine.QueueCompleted += () => Dispatcher.UIThread.Post(OnQueueCompleted);
        services.Engine.ItemFailed += (item, ex) => services.Log.AddError("Queue", $"{item.Name}: {ex.Message}");
        services.Scheduler.Triggered += entry => Dispatcher.UIThread.Post(() => OnSchedule(entry));
    }

    public BrowserPaneViewModel Left { get; }
    public BrowserPaneViewModel Right { get; }
    public QueueViewModel Queue { get; }
    public LogViewModel Log { get; }
    public CommandBarViewModel CommandBar { get; }

    /// <summary>0 = queue, 1 = log.</summary>
    [ObservableProperty] public partial int BottomTabIndex { get; set; }

    public bool IsGerman => Lang.German;
    public IReadOnlyList<Site> Sites => _services.Sites.Sites.OrderBy(s => s.Group).ThenBy(s => s.Name).ToList();

    public BrowserPaneViewModel ActivePane => Left.IsActive ? Left : Right;

    /// <summary>Raised when the app should close (e.g. after the queue finished).</summary>
    public event Action? CloseRequested;

    public void Activate(BrowserPaneViewModel pane)
    {
        Left.IsActive = pane == Left;
        Right.IsActive = pane == Right;
        OnPropertyChanged(nameof(ActivePane));
    }

    public async Task InitializeAsync()
    {
        if (_services.Sites.UsesMasterPassword && !await UnlockAsync())
        {
            CloseRequested?.Invoke();
            return;
        }

        var lastLocal = _services.Settings.Current.LastLocalPath;
        await Left.InitializeLocalAsync(lastLocal);
        await Right.InitializeLocalAsync(lastLocal);
        Left.NotifySitesChanged();
        Right.NotifySitesChanged();
        OnPropertyChanged(nameof(Sites));

        try
        {
            if (File.Exists(_services.Paths.QueueFile)) _services.Queue.Load(_services.Paths.QueueFile);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _services.Log.AddError("Queue", Lang.T("Gespeicherte Queue nicht lesbar: ", "Saved queue unreadable: ") + ex.Message);
        }
        _services.Scheduler.Start();
        _services.Log.AddInfo("DackelFXP", Lang.T($"Bereit. Konfiguration: {_services.Paths.Root}", $"Ready. Configuration: {_services.Paths.Root}"));

        // --left / --right <site name>: connect at startup.
        foreach (var (option, pane) in new[] { ("left", Left), ("right", Right) })
        {
            if (Program.Option(option) is not { } name) continue;
            var site = _services.Sites.Sites.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (site is null) _services.Log.AddError("DackelFXP", Lang.T($"Site '{name}' nicht gefunden", $"Site '{name}' not found"));
            else await pane.ConnectAsync(site);
        }
    }

    private async Task<bool> UnlockAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var password = await _services.Dialogs.PromptAsync(Lang.T("Master-Passwort", "Master password"), Lang.T("Master-Passwort für die Site-Liste:", "Master password for the site list:"), password: true);
            if (password is null) return false;
            if (_services.Sites.Unlock(password)) return true;
            await _services.Dialogs.ShowMessageAsync(Lang.T("Master-Passwort", "Master password"), Lang.T("Falsches Passwort.", "Wrong password."));
        }
        return false;
    }

    // ---------------------------------------------------------------- transfers

    private async Task TransferAsync(BrowserPaneViewModel from, IReadOnlyList<FileEntryViewModel> entries, bool startNow)
    {
        var to = from.Other!;
        if (!from.IsRemote && !to.IsRemote)
        {
            await _services.Dialogs.ShowMessageAsync(Lang.T("Übertragen", "Transfer"), Lang.T("Beide Seiten sind lokal. Bitte auf einer Seite mit einer Site verbinden.", "Both panes are local. Please connect one pane to a site."));
            return;
        }
        if (from.CurrentPath.Length == 0 || to.CurrentPath.Length == 0) return;

        var kind = (from.IsRemote, to.IsRemote) switch
        {
            (true, true) => TransferKind.Fxp,
            (true, false) => TransferKind.Download,
            _ => TransferKind.Upload,
        };
        var items = entries.Where(e => !e.IsDrive).Select(e => new QueueItem
        {
            Kind = kind,
            IsDirectory = e.IsDirectory,
            Size = e.IsDirectory ? 0 : e.Entry.Size,
            Modified = e.Entry.Modified,
            Source = Endpoint(from, from.Browser.Combine(from.CurrentPath, e.Name)),
            Target = Endpoint(to, to.Browser.Combine(to.CurrentPath, e.Name)),
        }).ToList();
        _services.Queue.Add(items);
        if (startNow) Queue.Start();
    }

    private Task UploadDroppedAsync(BrowserPaneViewModel target, IReadOnlyList<string> paths)
    {
        if (target.Site is null) return Task.CompletedTask;
        var items = paths.Where(p => File.Exists(p) || Directory.Exists(p)).Select(p => new QueueItem
        {
            Kind = TransferKind.Upload,
            IsDirectory = Directory.Exists(p),
            Size = File.Exists(p) ? new FileInfo(p).Length : 0,
            Source = new TransferEndpoint(null, p),
            Target = Endpoint(target, target.Browser.Combine(target.CurrentPath, Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)))),
        }).ToList();
        _services.Queue.Add(items);
        Queue.Start();
        return Task.CompletedTask;
    }

    private static TransferEndpoint Endpoint(BrowserPaneViewModel pane, string path) =>
        pane.Site is { } site ? new TransferEndpoint(site.Id, path, site.DisplayName) : new TransferEndpoint(null, path);

    private void OnQueueCompleted()
    {
        _services.Log.AddInfo("Queue", Lang.T("Queue abgearbeitet", "Queue finished"));
        _ = Left.RefreshAsync();
        _ = Right.RefreshAsync();
        if (_services.Settings.Current.OnQueueComplete == CompletionAction.Exit) CloseRequested?.Invoke();
    }

    private void OnSchedule(ScheduleEntry entry)
    {
        _services.Log.AddInfo(Lang.T("Planer", "Scheduler"), Lang.T($"Ausgelöst: {entry.Name} ({entry.Action})", $"Triggered: {entry.Name} ({entry.Action})"));
        if (entry.Action == ScheduleAction.StopQueue)
        {
            _ = _services.Engine.StopAsync();
            return;
        }
        if (!string.IsNullOrWhiteSpace(entry.QueueFile) && File.Exists(entry.QueueFile))
            _services.Queue.Load(entry.QueueFile);
        Queue.Start();
    }

    // ---------------------------------------------------------------- menus

    [RelayCommand]
    private async Task OpenSiteManagerAsync()
    {
        var vm = new SiteManagerViewModel(_services);
        var result = await _services.Dialogs.ShowDialogAsync<SiteManagerResult?>(new SiteManagerWindow { DataContext = vm });
        if (result is { } r) await (r.RightPane ? Right : Left).ConnectAsync(r.Site);
    }

    [RelayCommand]
    private async Task QuickConnectAsync()
    {
        var vm = new QuickConnectViewModel(_services.Settings.Current.QuickConnectHistory);
        var site = await _services.Dialogs.ShowDialogAsync<Site?>(new QuickConnectWindow { DataContext = vm });
        if (site is null) return;
        _services.Sites.AddTemporary(site);
        var history = _services.Settings.Current.QuickConnectHistory;
        history.Remove(vm.HistoryKey);
        history.Insert(0, vm.HistoryKey);
        if (history.Count > 20) history.RemoveRange(20, history.Count - 20);
        _services.Settings.Save();
        await ActivePane.ConnectAsync(site);
    }

    [RelayCommand]
    private Task ConnectSiteAsync(Site site) => ActivePane.ConnectAsync(site);

    [RelayCommand]
    private void FocusCommandLine()
    {
        BottomTabIndex = 1;
        if (ActivePane.IsRemote) CommandBar.Target = ActivePane;
        CommandBar.RequestFocus();
    }

    [RelayCommand]
    private void SetLanguage(string code)
    {
        _services.Settings.Current.Language = code;
        _services.Settings.Save();
        Lang.Code = code;
    }

    private void OnLanguageChanged()
    {
        App.ApplyLanguage();
        OnPropertyChanged(nameof(IsGerman));
        Left.RefreshTexts();
        Right.RefreshTexts();
        Queue.RefreshTexts();
        Log.RefreshTexts();
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        var vm = new SettingsViewModel(_services);
        await _services.Dialogs.ShowDialogAsync(new SettingsWindow { DataContext = vm });
        Left.NotifySitesChanged();
        Right.NotifySitesChanged();
        CommandBar.RefreshCustomCommands();
        Lang.Code = _services.Settings.Current.Language;
    }

    [RelayCommand]
    private void CompareFolders()
    {
        static IEnumerable<CompareEntry> ToCompare(BrowserPaneViewModel p) =>
            p.AllEntries.Select(e => new CompareEntry(e.Name, e.IsDirectory, e.Entry.Size, e.Entry.Modified));
        var (left, right) = DirectoryComparer.Compare(ToCompare(Left), ToCompare(Right));
        foreach (var e in Left.AllEntries) e.Compare = left.TryGetValue(e.Name, out var s) ? s : null;
        foreach (var e in Right.AllEntries) e.Compare = right.TryGetValue(e.Name, out var s) ? s : null;
        var diff = left.Values.Count(v => v != CompareStatus.Same) + right.Values.Count(v => v == CompareStatus.OnlyRight);
        _services.Log.AddInfo(Lang.T("Vergleich", "Compare"), Lang.T($"{Left.CurrentPath} ↔ {Right.CurrentPath}: {diff} Unterschiede", $"{Left.CurrentPath} ↔ {Right.CurrentPath}: {diff} differences"));
    }

    [RelayCommand]
    private void ClearCompare()
    {
        foreach (var e in Left.AllEntries.Concat(Right.AllEntries)) e.Compare = null;
    }

    [RelayCommand]
    private Task TransferDifferencesAsync()
    {
        CompareFolders();
        var pane = ActivePane;
        return pane.TransferAsync(pane.DifferentEntries(), startNow: false);
    }

    [RelayCommand]
    private async Task SetMasterPasswordAsync()
    {
        var password = await _services.Dialogs.PromptAsync(Lang.T("Master-Passwort", "Master password"),
            Lang.T("Neues Master-Passwort (leer lassen zum Entfernen).\nOhne Master-Passwort wird ein lokaler Schlüssel verwendet.",
                "New master password (leave empty to remove).\nWithout a master password a local key file is used."), password: true);
        if (password is null) return;
        if (password.Length > 0)
        {
            var repeat = await _services.Dialogs.PromptAsync(Lang.T("Master-Passwort", "Master password"), Lang.T("Passwort wiederholen:", "Repeat password:"), password: true);
            if (repeat != password)
            {
                await _services.Dialogs.ShowMessageAsync(Lang.T("Master-Passwort", "Master password"), Lang.T("Die Passwörter stimmen nicht überein.", "The passwords do not match."));
                return;
            }
        }
        _services.Sites.SetMasterPassword(password.Length == 0 ? null : password);
        await _services.Dialogs.ShowMessageAsync(Lang.T("Master-Passwort", "Master password"), password.Length == 0 ? Lang.T("Master-Passwort entfernt.", "Master password removed.") : Lang.T("Master-Passwort gesetzt.", "Master password set."));
    }

    [RelayCommand]
    private async Task DisconnectAllAsync()
    {
        await _services.Connections.DisconnectAllAsync();
        _services.Log.AddInfo("DackelFXP", Lang.T("Alle Verbindungen getrennt", "All connections closed"));
    }

    [RelayCommand]
    private Task AboutAsync() => _services.Dialogs.ShowMessageAsync(Lang.T("Über DackelFXP", "About DackelFXP"),
        $"DackelFXP – FTP/FTPS/FXP-Client\n.NET {Environment.Version}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\n\n" +
        Lang.T("Unterstützt: FTP, FTPS (explizit/implizit), Secure FXP (SSCN/CPSV), PRET, MLSD/STAT -l, Queue, Skiplisten, Planer.", "Supports: FTP, FTPS (explicit/implicit), secure FXP (SSCN/CPSV), PRET, MLSD/STAT -l, queue, skiplists, scheduler."));

    [RelayCommand]
    private void Exit() => CloseRequested?.Invoke();

}
