using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DackelFxp.App.Services;
using DackelFxp.Core.Sites;
using DackelFxp.Ftp;
using DackelFxp.Ftp.Transfer;

namespace DackelFxp.App.ViewModels;

public sealed record SiteManagerResult(Site Site, bool RightPane);

public sealed partial class SiteTreeNode : ObservableObject
{
    public required string Name { get; init; }
    public string GroupPath { get; init; } = "";
    public Site? Site { get; init; }
    public bool IsGroup => Site is null;
    public ObservableCollection<SiteTreeNode> Children { get; } = [];
    [ObservableProperty] public partial bool IsExpanded { get; set; } = true;
    public string Icon => IsGroup ? "FolderIcon" : "DriveIcon";
}

public sealed partial class SiteManagerViewModel : ObservableObject
{
    private readonly AppServices _services;

    public SiteManagerViewModel(AppServices services)
    {
        _services = services;
        Rebuild(null);
    }

    public ObservableCollection<SiteTreeNode> Roots { get; } = [];
    public IReadOnlyList<string> Groups => _services.Sites.Groups.Order().ToList();
    public IReadOnlyList<string> SkipLists => [.. _services.Settings.Current.SkipLists.Select(s => s.Name)];

    public static FtpTlsMode[] TlsModes { get; } = Enum.GetValues<FtpTlsMode>();
    public static TlsVersion[] TlsVersions { get; } = Enum.GetValues<TlsVersion>();
    public static FtpDataMode[] DataModes { get; } = Enum.GetValues<FtpDataMode>();
    public static FtpListMethod[] ListMethods { get; } = Enum.GetValues<FtpListMethod>();
    public static FtpPretMode[] PretModes { get; } = Enum.GetValues<FtpPretMode>();
    public static FxpSecureMode[] FxpSecureModes { get; } = Enum.GetValues<FxpSecureMode>();
    public static FxpPassiveSide[] FxpPassiveSides { get; } = Enum.GetValues<FxpPassiveSide>();
    public static string[] Encodings { get; } = ["auto", "utf-8", "iso-8859-1", "windows-1252", "ibm437"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSite))]
    public partial SiteTreeNode? SelectedNode { get; set; }

    /// <summary>Copy of the selected site being edited; written back on save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSite))]
    public partial Site? EditSite { get; private set; }

    [ObservableProperty] public partial Bookmark? SelectedBookmark { get; set; }

    public bool HasSite => EditSite is not null;

    /// <summary>Set by the window to close with a result.</summary>
    public Action<SiteManagerResult?>? Close { get; set; }

    partial void OnSelectedNodeChanged(SiteTreeNode? value)
    {
        EditSite = value?.Site?.Clone();
        OnPropertyChanged(nameof(Bookmarks));
    }

    public ObservableCollection<Bookmark> Bookmarks => EditSite is null ? [] : new(EditSite.Bookmarks);

    private void Rebuild(Guid? select)
    {
        Roots.Clear();
        var groupNodes = new Dictionary<string, SiteTreeNode>();

        SiteTreeNode GroupNode(string path)
        {
            if (groupNodes.TryGetValue(path, out var existing)) return existing;
            var slash = path.LastIndexOf('/');
            var node = new SiteTreeNode { Name = slash < 0 ? path : path[(slash + 1)..], GroupPath = path };
            groupNodes[path] = node;
            if (slash < 0) Roots.Add(node);
            else GroupNode(path[..slash]).Children.Add(node);
            return node;
        }

        foreach (var group in _services.Sites.Groups.Order()) GroupNode(group);
        SiteTreeNode? selected = null;
        foreach (var site in _services.Sites.Sites.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var node = new SiteTreeNode { Name = site.DisplayName, GroupPath = site.Group, Site = site };
            if (site.Group.Length == 0) Roots.Add(node);
            else GroupNode(site.Group).Children.Add(node);
            if (site.Id == select) selected = node;
        }
        OnPropertyChanged(nameof(Groups));
        SelectedNode = selected;
    }

    [RelayCommand]
    private void NewSite()
    {
        var site = new Site { Group = SelectedNode?.IsGroup == true ? SelectedNode.GroupPath : SelectedNode?.GroupPath ?? "" };
        _services.Sites.AddOrUpdate(site);
        Rebuild(site.Id);
    }

    [RelayCommand]
    private async Task NewGroupAsync()
    {
        var parent = SelectedNode?.GroupPath ?? "";
        var name = await _services.Dialogs.PromptAsync("Neue Gruppe", parent.Length > 0 ? $"Name der Untergruppe von '{parent}':" : "Name der Gruppe:");
        if (string.IsNullOrWhiteSpace(name)) return;
        _services.Sites.AddGroup(parent.Length > 0 ? $"{parent}/{name.Trim()}" : name.Trim());
        Rebuild(EditSite?.Id);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedNode is not { } node) return;
        if (node.Site is { } site)
        {
            if (!await _services.Dialogs.ConfirmAsync("Site löschen", $"'{site.DisplayName}' löschen?")) return;
            _services.Sites.Remove(site.Id);
        }
        else
        {
            if (!await _services.Dialogs.ConfirmAsync("Gruppe löschen", $"Gruppe '{node.GroupPath}' löschen? Enthaltene Sites wandern in die oberste Ebene.")) return;
            _services.Sites.RemoveGroup(node.GroupPath);
        }
        Rebuild(null);
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (EditSite is null) return;
        var copy = EditSite.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name += " (Kopie)";
        copy.TrustedCertificate = null;
        _services.Sites.AddOrUpdate(copy);
        Rebuild(copy.Id);
    }

    [RelayCommand]
    private void Save()
    {
        if (EditSite is null) return;
        _services.Sites.AddOrUpdate(EditSite);
        Rebuild(EditSite.Id);
    }

    [RelayCommand]
    private void RemoveBookmark()
    {
        if (EditSite is null || SelectedBookmark is null) return;
        EditSite.Bookmarks.Remove(SelectedBookmark);
        OnPropertyChanged(nameof(Bookmarks));
    }

    [RelayCommand]
    private void ConnectLeft() => ConnectSide(false);

    [RelayCommand]
    private void ConnectRight() => ConnectSide(true);

    private void ConnectSide(bool right)
    {
        if (EditSite is null) return;
        _services.Sites.AddOrUpdate(EditSite);
        Close?.Invoke(new SiteManagerResult(_services.Sites.Find(EditSite.Id)!, right));
    }

    [RelayCommand]
    private void Cancel() => Close?.Invoke(null);
}

public sealed partial class QuickConnectViewModel : ObservableObject
{
    public QuickConnectViewModel(IReadOnlyList<string> history)
    {
        History = history;
    }

    public IReadOnlyList<string> History { get; }
    public static FtpTlsMode[] TlsModes { get; } = Enum.GetValues<FtpTlsMode>();

    [ObservableProperty] public partial string Host { get; set; } = "";
    [ObservableProperty] public partial int Port { get; set; } = 21;
    [ObservableProperty] public partial string User { get; set; } = "anonymous";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial FtpTlsMode TlsMode { get; set; } = FtpTlsMode.Explicit;
    [ObservableProperty] public partial string? SelectedHistory { get; set; }

    public Action<Site?>? Close { get; set; }

    /// <summary>History entry without password: "tls|user@host:port".</summary>
    public string HistoryKey => $"{TlsMode}|{User}@{Host}:{Port}";

    partial void OnSelectedHistoryChanged(string? value)
    {
        if (value is null) return;
        var bar = value.IndexOf('|');
        var at = value.LastIndexOf('@');
        var colon = value.LastIndexOf(':');
        if (bar < 0 || at < 0 || colon < at) return;
        if (Enum.TryParse<FtpTlsMode>(value[..bar], out var tls)) TlsMode = tls;
        User = value[(bar + 1)..at];
        Host = value[(at + 1)..colon];
        if (int.TryParse(value[(colon + 1)..], out var port)) Port = port;
    }

    partial void OnTlsModeChanged(FtpTlsMode value)
    {
        if (value == FtpTlsMode.Implicit && Port == 21) Port = 990;
        else if (value != FtpTlsMode.Implicit && Port == 990) Port = 21;
    }

    [RelayCommand]
    private void Connect()
    {
        if (string.IsNullOrWhiteSpace(Host)) return;
        Close?.Invoke(new Site
        {
            Name = $"{Host}:{Port}",
            Host = Host.Trim(),
            Port = Port,
            User = User,
            Password = Password,
            TlsMode = TlsMode,
        });
    }

    [RelayCommand]
    private void Cancel() => Close?.Invoke(null);
}
