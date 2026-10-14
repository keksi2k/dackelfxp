using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DackelFxp.App.Services;
using DackelFxp.Core.Rules;
using DackelFxp.Core.Settings;
using DackelFxp.Ftp;

namespace DackelFxp.App.ViewModels;

public sealed partial class ScheduleEntryViewModel(ScheduleEntry entry) : ObservableObject
{
    public ScheduleEntry Entry { get; } = entry;

    public string Name { get => Entry.Name; set { Entry.Name = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public bool Enabled { get => Entry.Enabled; set { Entry.Enabled = value; OnPropertyChanged(); } }
    public ScheduleAction Action { get => Entry.Action; set { Entry.Action = value; OnPropertyChanged(); OnPropertyChanged(nameof(Summary)); } }
    public string? QueueFile { get => Entry.QueueFile; set => Entry.QueueFile = value; }

    public string TimeText
    {
        get => Entry.Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        set
        {
            if (TimeOnly.TryParseExact(value, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                Entry.Time = t;
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string DateText
    {
        get => Entry.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        set => Entry.Date = DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    private bool Day(DayOfWeek d) => Entry.Days.Contains(d);

    private void SetDay(DayOfWeek d, bool on)
    {
        Entry.Days.Remove(d);
        if (on) Entry.Days.Add(d);
        OnPropertyChanged(nameof(Summary));
    }

    public bool Mon { get => Day(DayOfWeek.Monday); set => SetDay(DayOfWeek.Monday, value); }
    public bool Tue { get => Day(DayOfWeek.Tuesday); set => SetDay(DayOfWeek.Tuesday, value); }
    public bool Wed { get => Day(DayOfWeek.Wednesday); set => SetDay(DayOfWeek.Wednesday, value); }
    public bool Thu { get => Day(DayOfWeek.Thursday); set => SetDay(DayOfWeek.Thursday, value); }
    public bool Fri { get => Day(DayOfWeek.Friday); set => SetDay(DayOfWeek.Friday, value); }
    public bool Sat { get => Day(DayOfWeek.Saturday); set => SetDay(DayOfWeek.Saturday, value); }
    public bool Sun { get => Day(DayOfWeek.Sunday); set => SetDay(DayOfWeek.Sunday, value); }

    public string Summary => $"{(Name.Length > 0 ? Name : Action.ToString())} – {TimeText}" +
                             (Entry.Days.Count == 0 ? Lang.T(" (einmalig)", " (once)") : " " + string.Join(",", Entry.Days.Order().Select(d => d.ToString()[..2])));
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        Settings = services.Settings.Current;
        SkipLists = new(Settings.SkipLists);
        SelectedSkipList = SkipLists.FirstOrDefault();
        Commands = new(Settings.CustomCommands);
        Schedules = new(Settings.Schedules.Select(s => new ScheduleEntryViewModel(s)));
    }

    public AppSettings Settings { get; }

    public static ExistsAction[] ExistsActions { get; } = Enum.GetValues<ExistsAction>();
    public static CompletionAction[] CompletionActions { get; } = Enum.GetValues<CompletionAction>();
    public static RuleTarget[] RuleTargets { get; } = Enum.GetValues<RuleTarget>();
    public static RuleAction[] RuleActions { get; } = Enum.GetValues<RuleAction>();
    public static ScheduleAction[] ScheduleActions { get; } = Enum.GetValues<ScheduleAction>();
    public static string[] Themes { get; } = ["Dark", "Light", "System"];
    public static string[] Languages { get; } = ["de", "en"];

    /// <summary>Installed font families for the font selection.</summary>
    public static IReadOnlyList<string> FontNames { get; } =
        [.. Avalonia.Media.FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase)];

    public ObservableCollection<SkipList> SkipLists { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Rules))]
    public partial SkipList? SelectedSkipList { get; set; }

    public ObservableCollection<SkipRule> Rules => SelectedSkipList is null ? [] : _rules ??= new(SelectedSkipList.Rules);
    private ObservableCollection<SkipRule>? _rules;

    partial void OnSelectedSkipListChanging(SkipList? value) => FlushRules();

    private void FlushRules()
    {
        if (SelectedSkipList is not null && _rules is not null) SelectedSkipList.Rules = [.. _rules];
        _rules = null;
    }

    [ObservableProperty] public partial SkipRule? SelectedRule { get; set; }

    public ObservableCollection<CustomCommand> Commands { get; }
    [ObservableProperty] public partial CustomCommand? SelectedCommand { get; set; }

    public ObservableCollection<ScheduleEntryViewModel> Schedules { get; }
    [ObservableProperty] public partial ScheduleEntryViewModel? SelectedSchedule { get; set; }

    public Action? Close { get; set; }

    [RelayCommand]
    private async Task AddSkipListAsync()
    {
        var name = await _services.Dialogs.PromptAsync(Lang.T("Skipliste", "Skiplist"), Lang.T("Name der neuen Skipliste:", "Name of the new skiplist:"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var list = new SkipList { Name = name.Trim() };
        SkipLists.Add(list);
        SelectedSkipList = list;
    }

    [RelayCommand]
    private void RemoveSkipList()
    {
        if (SelectedSkipList is null || SkipLists.Count <= 1) return;
        _rules = null;
        SkipLists.Remove(SelectedSkipList);
        SelectedSkipList = SkipLists.FirstOrDefault();
    }

    [RelayCommand]
    private void AddRule()
    {
        if (SelectedSkipList is null) return;
        var rule = new SkipRule { Pattern = "*.tmp" };
        Rules.Add(rule);
        SelectedRule = rule;
    }

    [RelayCommand]
    private void RemoveRule()
    {
        if (SelectedRule is not null) Rules.Remove(SelectedRule);
    }

    [RelayCommand]
    private void AddCommand()
    {
        var command = new CustomCommand { Name = Lang.T("Neuer Befehl", "New command"), Commands = "SITE " };
        Commands.Add(command);
        SelectedCommand = command;
    }

    [RelayCommand]
    private void RemoveCommand()
    {
        if (SelectedCommand is not null) Commands.Remove(SelectedCommand);
    }

    [RelayCommand]
    private void AddSchedule()
    {
        var entry = new ScheduleEntryViewModel(new ScheduleEntry { Name = Lang.T("Queue starten", "Start queue"), Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday] });
        Schedules.Add(entry);
        SelectedSchedule = entry;
    }

    [RelayCommand]
    private void RemoveSchedule()
    {
        if (SelectedSchedule is not null) Schedules.Remove(SelectedSchedule);
    }

    [RelayCommand]
    private async Task PickScheduleQueueAsync()
    {
        if (SelectedSchedule is null) return;
        var file = await _services.Dialogs.PickOpenFileAsync(Lang.T("Queue-Datei", "Queue file"), "*.json");
        if (file is not null)
        {
            SelectedSchedule.QueueFile = file;
            OnPropertyChanged(nameof(SelectedSchedule));
        }
    }

    [RelayCommand]
    private void Save()
    {
        FlushRules();
        Settings.SkipLists = [.. SkipLists];
        Settings.CustomCommands = [.. Commands];
        Settings.Schedules = [.. Schedules.Select(s => s.Entry)];
        if (Settings.FindSkipList(Settings.DefaultSkipList) is { } def) Settings.DefaultSkipList = def.Name;
        _services.Settings.Save();
        _services.Connections.ApplySpeedLimits();
        _services.ApplyIdent();
        App.ApplyTheme(Settings.Theme);
        App.ApplyFont(Settings.FontFamily, Settings.ListFontSize);
        Close?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        _services.Settings.Load();
        Close?.Invoke();
    }
}
