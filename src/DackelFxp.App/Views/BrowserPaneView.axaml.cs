using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DackelFxp.App.ViewModels;

namespace DackelFxp.App.Views;

public partial class BrowserPaneView : UserControl
{
    private const double DragThreshold = 8;

    private bool _pressed;
    private Point _pressPoint;
    private bool _dragging;
    private IReadOnlyList<FileEntryViewModel> _dragEntries = [];
    private BrowserPaneView? _hoverTarget;

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

        // Tunnel + handledEventsToo: the DataGrid handles (and captures) pointer and key input itself.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, (_, _) => Activated?.Invoke(), RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => { if (_dragging) EndDrag(); }, handledEventsToo: true);

        // Files dragged in from the system file manager (Explorer, Nautilus, ...) are uploaded.
        Grid.AddHandler(DragDrop.DragOverEvent, OnExternalDragOver);
        Grid.AddHandler(DragDrop.DropEvent, OnExternalDrop);
    }

    /// <summary>The pane got focus or was clicked: it becomes the active pane.</summary>
    public event Action? Activated;

    /// <summary>True while another pane drags entries over this one.</summary>
    internal bool IsDropHighlighted => DropOverlay.IsVisible;

    private BrowserPaneViewModel? Vm => DataContext as BrowserPaneViewModel;

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.Source is TextBox) return;
        if (e.Key == Key.Escape && _dragging)
        {
            EndDrag();
            e.Handled = true;
            return;
        }
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

    // ---------------------------------------------------------------- mouse

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Activated?.Invoke();
        if (Vm is not { } vm) return;
        var props = e.GetCurrentPoint(this).Properties;

        // Middle button (mouse 3): one folder up; "back" side button: previous folder.
        if (props.IsMiddleButtonPressed)
        {
            e.Handled = true;
            await vm.UpCommand.ExecuteAsync(null);
            return;
        }
        if (props.IsXButton1Pressed)
        {
            e.Handled = true;
            await vm.BackCommand.ExecuteAsync(null);
            return;
        }

        // Left button on a file row: a drag may start.
        _pressed = props.IsLeftButtonPressed
                   && e.Source is Visual v && v.FindAncestorOfType<DataGridRow>(includeSelf: true) is not null;
        if (_pressed) _pressPoint = e.GetPosition(this);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_pressed || Vm is not { } vm) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (_dragging) EndDrag();
            _pressed = false;
            return;
        }

        if (!_dragging)
        {
            var delta = e.GetPosition(this) - _pressPoint;
            if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold) return;
            _dragEntries = vm.Selection.Where(s => !s.IsDrive).ToList();
            if (_dragEntries.Count == 0)
            {
                _pressed = false;
                return;
            }
            _dragging = true;
            // Own the pointer until release, wherever it goes (also over the other pane).
            e.Pointer.Capture(this);
            Cursor = new Cursor(StandardCursorType.DragCopy);
        }

        SetHoverTarget(PaneAt(e));
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressed = false;
        if (!_dragging) return;
        var target = PaneAt(e);
        var entries = _dragEntries;
        EndDrag();
        e.Handled = true;
        if (target is not null && Vm is { } vm && target.Vm is { } targetVm && targetVm == vm.Other)
            await vm.TransferAsync(entries, startNow: true);
    }

    /// <summary>The other pane under the pointer (dropping onto the own pane does nothing).</summary>
    private BrowserPaneView? PaneAt(PointerEventArgs e)
    {
        // Geometric check instead of InputHitTest: the window's overlay layer would win the hit test.
        if (TopLevel.GetTopLevel(this) is not { } top) return null;
        foreach (var pane in top.GetVisualDescendants().OfType<BrowserPaneView>())
        {
            if (pane == this || !pane.IsEffectivelyVisible) continue;
            if (new Rect(pane.Bounds.Size).Contains(e.GetPosition(pane))) return pane;
        }
        return null;
    }

    private void SetHoverTarget(BrowserPaneView? target)
    {
        if (_hoverTarget == target) return;
        _hoverTarget?.DropOverlay.SetCurrentValue(IsVisibleProperty, false);
        _hoverTarget = target;
        _hoverTarget?.DropOverlay.SetCurrentValue(IsVisibleProperty, true);
    }

    private void EndDrag()
    {
        _dragging = false;
        _pressed = false;
        _dragEntries = [];
        SetHoverTarget(null);
        Cursor = Cursor.Default;
    }

    // ---------------------------------------------------------------- external drops

    private void OnExternalDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = Vm is { IsRemote: true } && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnExternalDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { IsRemote: true } vm || e.DataTransfer.TryGetFiles() is not { } files) return;
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0 && vm.ExternalDropHandler is { } handler) await handler(vm, paths);
    }
}
