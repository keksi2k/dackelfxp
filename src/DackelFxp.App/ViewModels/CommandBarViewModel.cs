using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DackelFxp.App.Services;
using DackelFxp.Core.Settings;
using DackelFxp.Ftp;

namespace DackelFxp.App.ViewModels;

/// <summary>Command line below the log: sends raw or custom commands to one pane's site; replies appear in the log.</summary>
public sealed partial class CommandBarViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly List<string> _history = [];
    private int _historyIndex;

    public CommandBarViewModel(AppServices services, BrowserPaneViewModel left, BrowserPaneViewModel right)
    {
        _services = services;
        Targets = [left, right];
        Target = left;
        foreach (var pane in Targets)
        {
            pane.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BrowserPaneViewModel.Browser)) OnPropertyChanged(nameof(CustomCommands));
            };
        }
    }

    public IReadOnlyList<BrowserPaneViewModel> Targets { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CustomCommands))]
    public partial BrowserPaneViewModel? Target { get; set; }

    [ObservableProperty] public partial string CommandText { get; set; } = "";

    public IReadOnlyList<CustomCommand> CustomCommands => Target?.CustomCommands ?? [];

    /// <summary>The view focuses the input box.</summary>
    public event Action? FocusRequested;

    public void RequestFocus() => FocusRequested?.Invoke();

    public void RefreshCustomCommands() => OnPropertyChanged(nameof(CustomCommands));

    /// <summary>Uses the chosen pane if it is connected, otherwise the other one.</summary>
    private BrowserPaneViewModel? ResolveTarget()
    {
        if (Target is { IsRemote: true }) return Target;
        var other = Targets.FirstOrDefault(t => t.IsRemote);
        if (other is not null) Target = other;
        return other;
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var command = CommandText.Trim();
        if (command.Length == 0) return;
        var target = ResolveTarget();
        if (target is null)
        {
            _services.Log.AddError(Lang.T("Befehl", "Command"), Lang.T("Keine Seite ist mit einer Site verbunden.", "No pane is connected to a site."));
            return;
        }
        _history.Remove(command);
        _history.Add(command);
        _historyIndex = _history.Count;
        CommandText = "";
        await target.SendCommandsAsync([command]);
    }

    [RelayCommand]
    private async Task RunCustomAsync(CustomCommand command)
    {
        var target = ResolveTarget();
        if (target is null) return;
        await target.RunCustomCommandCommand.ExecuteAsync(command);
    }

    public void HistoryUp()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Max(0, _historyIndex - 1);
        CommandText = _history[_historyIndex];
    }

    public void HistoryDown()
    {
        if (_history.Count == 0) return;
        _historyIndex = Math.Min(_history.Count, _historyIndex + 1);
        CommandText = _historyIndex < _history.Count ? _history[_historyIndex] : "";
    }
}
