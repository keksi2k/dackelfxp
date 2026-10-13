using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DackelFxp.App.Services;
using DackelFxp.App.ViewModels;

namespace DackelFxp.App.Views;

public partial class LogView : UserControl
{
    private LogViewModel? _vm;

    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Appended -= ScrollToEnd;
            _vm = DataContext as LogViewModel;
            if (_vm is not null) _vm.Appended += ScrollToEnd;
        };
        // The log tab is created lazily and lines arrive while it is hidden: scroll when it becomes visible.
        AttachedToVisualTree += (_, _) => ScrollToEnd();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible) ScrollToEnd();
        };
        // Ctrl+C copies the selected lines.
        AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key != Key.C || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            var text = string.Join(Environment.NewLine, List.SelectedItems?.OfType<LogLine>().Select(l => $"{l.TimeText} [{l.Source}] {l.Text}") ?? []);
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && text.Length > 0)
                await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private void ScrollToEnd()
    {
        if (_vm is not { AutoScroll: true } vm || vm.Lines.Count == 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (vm.Lines.Count > 0) List.ScrollIntoView(vm.Lines[^1]);
        }, DispatcherPriority.Background);
    }
}
