using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DackelFxp.App.Services;
using DackelFxp.Ftp;

namespace DackelFxp.App.ViewModels;

public sealed partial class LogViewModel : ObservableObject
{
    private readonly AppServices _services;

    public LogViewModel(AppServices services)
    {
        _services = services;
        services.Log.Lines.CollectionChanged += OnSourceChanged;
    }

    public ObservableCollection<string> Sources => _services.Log.Sources;
    public ObservableCollection<LogLine> Lines { get; } = [];

    [ObservableProperty] public partial string SelectedSource { get; set; } = LogStore.AllSources;
    [ObservableProperty] public partial bool AutoScroll { get; set; } = true;

    partial void OnSelectedSourceChanged(string value) => Rebuild();

    /// <summary>Raised after lines were appended (the view scrolls to the end).</summary>
    public event Action? Appended;

    private bool Matches(LogLine line) =>
        SelectedSource is null or LogStore.AllSources || line.Source.StartsWith(SelectedSource + " #", StringComparison.Ordinal) || line.Source == SelectedSource;

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                foreach (LogLine line in e.NewItems!)
                    if (Matches(line)) Lines.Add(line);
                Appended?.Invoke();
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (LogLine line in e.OldItems!) Lines.Remove(line);
                break;
            default:
                Rebuild();
                break;
        }
    }

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var line in _services.Log.Lines.Where(Matches)) Lines.Add(line);
        Appended?.Invoke();
    }

    /// <summary>The "all" entry of the source filter is rendered by a converter; force re-rendering.</summary>
    public void RefreshTexts()
    {
        // Re-insert the "all" entry so its container is rebuilt with the new text.
        var selected = SelectedSource;
        Sources.RemoveAt(0);
        Sources.Insert(0, LogStore.AllSources);
        SelectedSource = selected;
    }

    [RelayCommand]
    private void Clear() => _services.Log.Clear();

    [RelayCommand]
    private async Task SaveAsync()
    {
        var file = await _services.Dialogs.PickSaveFileAsync(Lang.T("Log speichern", "Save log"), $"dackelfxp-{DateTime.Now:yyyyMMdd-HHmm}.log", "*.log");
        if (file is null) return;
        await File.WriteAllLinesAsync(file, Lines.Select(l => $"{l.Time:yyyy-MM-dd HH:mm:ss} [{l.Source}] {l.Kind,-7} {l.Text}"));
    }
}
