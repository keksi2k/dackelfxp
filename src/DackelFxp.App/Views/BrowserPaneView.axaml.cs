using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DackelFxp.App.ViewModels;

namespace DackelFxp.App.Views;

/// <summary>Drag payload between the two panes (in-process only).</summary>
public sealed record PaneDragData(BrowserPaneViewModel Source, IReadOnlyList<FileEntryViewModel> Entries);

public partial class BrowserPaneView : UserControl
{
    private static readonly DataFormat<PaneDragData> PaneFormat = DataFormat.CreateInProcessFormat<PaneDragData>("dackelfxp-entries");

    private PointerPressedEventArgs? _pressArgs;
    private Point _pressPoint;
    private bool _dragging;

    public BrowserPaneView()
    {
        InitializeComponent();

        Grid.SelectionChanged += (_, _) =>
        {
            if (Vm is { } vm) vm.Selection = Grid.SelectedItems.OfType<FileEntryViewModel>().ToList();
        };
        Grid.DoubleTapped += async (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<DataGridColumnHeader>() is not null) return;
            if (Vm is { } vm && Grid.SelectedItem is FileEntryViewModel entry) await vm.OpenCommand.ExecuteAsync(entry);
        };

        // Tunnel: the DataGrid marks key and pointer events handled itself.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, (_, _) => Activated?.Invoke(), RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerReleasedEvent, (_, _) => _pressArgs = null, RoutingStrategies.Tunnel);

        Grid.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Grid.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>The pane got focus or was clicked: it becomes the active pane.</summary>
    public event Action? Activated;

    private BrowserPaneViewModel? Vm => DataContext as BrowserPaneViewModel;

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Source is TextBox) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        Func<Task>? action = (e.Key, ctrl) switch
        {
            (Key.Enter, false) => () => vm.OpenCommand.ExecuteAsync(Grid.SelectedItem as FileEntryViewModel),
            (Key.Back, false) => () => vm.UpCommand.ExecuteAsync(null),
            (Key.F5, false) => () => vm.RefreshCommand.ExecuteAsync(null),
            (Key.Delete, false) => () => vm.DeleteCommand.ExecuteAsync(null),
            (Key.F2, false) => () => vm.RenameCommand.ExecuteAsync(null),
            (Key.F3, false) => () => vm.ViewEditCommand.ExecuteAsync(null),
            (Key.F7, false) => () => vm.MakeDirectoryCommand.ExecuteAsync(null),
            (Key.T, true) => () => vm.TransferSelectedCommand.ExecuteAsync(null),
            (Key.Q, true) => () => vm.QueueSelectedCommand.ExecuteAsync(null),
            (Key.L, true) => () => { PathBox.Focus(); PathBox.SelectAll(); return Task.CompletedTask; },
            _ => null,
        };
        if (action is null) return;
        e.Handled = true;
        await action();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Activated?.Invoke();
        if (!e.GetCurrentPoint(Grid).Properties.IsLeftButtonPressed) return;
        _pressArgs = e;
        _pressPoint = e.GetPosition(Grid);
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressArgs is null || _dragging || Vm is not { } vm) return;
        if (!e.GetCurrentPoint(Grid).Properties.IsLeftButtonPressed)
        {
            _pressArgs = null;
            return;
        }
        var delta = e.GetPosition(Grid) - _pressPoint;
        if (Math.Abs(delta.X) < 8 && Math.Abs(delta.Y) < 8) return;
        var entries = vm.Selection.Where(s => !s.IsDrive).ToList();
        if (entries.Count == 0) return;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(PaneFormat, new PaneDragData(vm, entries)));
        _dragging = true;
        try { await DragDrop.DoDragDropAsync(_pressArgs, transfer, DragDropEffects.Copy); }
        finally
        {
            _dragging = false;
            _pressArgs = null;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;
        if (Vm is not { } vm) return;
        if (e.DataTransfer.TryGetValue(PaneFormat) is { } data)
        {
            if (data.Source != vm) e.DragEffects = DragDropEffects.Copy;
        }
        else if (vm.IsRemote && e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.DataTransfer.TryGetValue(PaneFormat) is { } data)
        {
            if (data.Source != vm) await data.Source.TransferAsync(data.Entries, startNow: true);
            return;
        }
        if (vm.IsRemote && e.DataTransfer.TryGetFiles() is { } files)
        {
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0 && vm.ExternalDropHandler is { } handler) await handler(vm, paths);
        }
    }
}
