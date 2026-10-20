using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using DackelFxp.App.ViewModels;
using DackelFxp.Core.Settings;
using DackelFxp.Ftp;

namespace DackelFxp.App.Views;

public partial class BrowserPaneView : UserControl
{
    private const double DragThreshold = 8;
    /// <summary>Pause after which typed letters start a new search (like Explorer).</summary>
    private const long TypeAheadResetMs = 1000;

    private bool _pressed;
    private Point _pressPoint;
    private bool _dragging;
    private IReadOnlyList<FileEntryViewModel> _dragEntries = [];
    private BrowserPaneView? _hoverTarget;
    /// <summary>Row pressed inside a multi selection: selected alone only if the press ends without a drag.</summary>
    private FileEntryViewModel? _deferredSelect;
    private string _typeAhead = "";
    private long _typeAheadAt;
    private readonly List<(DataGridColumn Column, DataGridLength Width)> _defaultColumns;

    public BrowserPaneView()
    {
        InitializeComponent();
        _defaultColumns = Grid.Columns.Select(c => (c, c.Width)).ToList();

        DataContextChanged += (_, _) =>
        {
            if (Vm is not { } vm) return;
            vm.SelectionSource = () => Grid.SelectedItems.OfType<FileEntryViewModel>().ToList();
            ApplyColumnLayout(ColumnSettings);
        };
        Grid.DoubleTapped += async (_, e) =>
        {
            if (e.Source is Visual v && v.FindAncestorOfType<DataGridColumnHeader>() is not null) return;
            if (Vm is { } vm && Grid.SelectedItem is FileEntryViewModel entry) await vm.OpenCommand.ExecuteAsync(entry);
        };

        // Tunnel + handledEventsToo: the DataGrid handles (and captures) pointer and key input itself.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        AddHandler(GotFocusEvent, (_, _) => Activated?.Invoke(), RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, (_, _) => { if (_dragging) EndDrag(); }, handledEventsToo: true);

        // Files dragged in from the system file manager (Explorer, Nautilus, ...) are uploaded.
        Grid.AddHandler(DragDrop.DragOverEvent, OnExternalDragOver);
        Grid.AddHandler(DragDrop.DropEvent, OnExternalDrop);

        // Right-click on a column header: column menu instead of the file menu.
        Grid.AddHandler(ContextRequestedEvent, OnHeaderContextRequested, RoutingStrategies.Tunnel);
        Grid.ColumnReordered += (_, _) => StoreColumnLayout();
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

    /// <summary>Typing in the list jumps to the first entry starting with the typed text.</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (Vm is not { } vm || e.Source is TextBox || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        var now = Environment.TickCount64;
        if (now - _typeAheadAt > TypeAheadResetMs) _typeAhead = "";
        _typeAheadAt = now;
        // A leading space keeps its usual meaning in the list.
        if (_typeAhead.Length == 0 && string.IsNullOrWhiteSpace(e.Text)) return;
        _typeAhead += e.Text;
        e.Handled = true;
        if (vm.FindByPrefix(_typeAhead, Grid.SelectedItem as FileEntryViewModel) is not { } match) return;
        Grid.SelectedItems.Clear();
        Grid.SelectedItem = match;
        Grid.ScrollIntoView(match, null);
    }

    // ---------------------------------------------------------------- columns

    private List<ListColumn> ColumnSettings
    {
        get
        {
            var s = App.Services.Settings.Current;
            return Vm?.IsLeft != false ? s.LeftColumns : s.RightColumns;
        }
    }

    private static string ColumnKey(DataGridColumn column) => column.Tag as string ?? "";

    private static string ColumnTitle(DataGridColumn column) => (column.Header as TextBlock)?.Text ?? ColumnKey(column);

    private void ApplyColumnLayout(IReadOnlyList<ListColumn> layout)
    {
        // Saved columns in their order, columns unknown to the settings (new in a later version) behind them.
        var ordered = layout
            .Select(l => (Setting: l, Column: Grid.Columns.FirstOrDefault(c => ColumnKey(c) == l.Key)))
            .Where(x => x.Column is not null)
            .ToList();
        var index = 0;
        foreach (var (setting, column) in ordered)
        {
            column!.DisplayIndex = index++;
            column.IsVisible = setting.Visible;
            if (setting.Width > 0) column.Width = new DataGridLength(setting.Width);
        }
        foreach (var column in Grid.Columns.Except(ordered.Select(x => x.Column!)).OrderBy(c => c.DisplayIndex).ToList())
            column.DisplayIndex = index++;
        if (Grid.Columns.All(c => !c.IsVisible)) Grid.Columns[0].IsVisible = true;
    }

    /// <summary>Writes order, visibility and width of the columns into the settings (saved on exit).</summary>
    public void StoreColumnLayout()
    {
        if (Vm is null) return;
        var layout = ColumnSettings;
        layout.Clear();
        layout.AddRange(Grid.Columns.OrderBy(c => c.DisplayIndex).Select(c => new ListColumn
        {
            Key = ColumnKey(c),
            Visible = c.IsVisible,
            // The star column (name) keeps filling the remaining width.
            Width = c.Width.IsAbsolute && c.ActualWidth > 0 ? c.ActualWidth : 0,
        }));
    }

    private void OnHeaderContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is not { } header) return;
        e.Handled = true;
        HeaderMenu = new ContextMenu { ItemsSource = BuildColumnMenu() };
        HeaderMenu.Open(header);
    }

    /// <summary>The column menu opened last.</summary>
    internal ContextMenu? HeaderMenu { get; private set; }

    internal List<object> BuildColumnMenu()
    {
        var visibleCount = Grid.Columns.Count(c => c.IsVisible);
        var items = new List<object>();
        foreach (var column in Grid.Columns.OrderBy(c => c.DisplayIndex))
        {
            var item = new MenuItem
            {
                Header = ColumnTitle(column),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
                // The last visible column cannot be hidden.
                IsEnabled = !(column.IsVisible && visibleCount == 1),
            };
            item.Click += (_, _) => SetColumnVisible(column, !column.IsVisible);
            items.Add(item);
        }
        items.Add(new Separator());
        var reset = new MenuItem { Header = Lang.T("Standardspalten", "Default columns") };
        reset.Click += (_, _) => ResetColumns();
        items.Add(reset);
        return items;
    }

    internal void SetColumnVisible(DataGridColumn column, bool visible)
    {
        if (!visible && Grid.Columns.Count(c => c.IsVisible) <= 1) return;
        column.IsVisible = visible;
        StoreColumnLayout();
    }

    internal void ResetColumns()
    {
        for (var i = 0; i < _defaultColumns.Count; i++)
        {
            var (column, width) = _defaultColumns[i];
            column.DisplayIndex = i;
            column.IsVisible = true;
            column.Width = width;
        }
        ColumnSettings.Clear();
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
        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
        _pressed = props.IsLeftButtonPressed && row is not null;
        _deferredSelect = null;
        if (!_pressed) return;
        _pressPoint = e.GetPosition(this);

        // Pressing an entry of a multi selection must keep the selection (like a file manager),
        // otherwise only that one entry would be dragged. The DataGrid would collapse it right here.
        if (row!.DataContext is FileEntryViewModel entry && e.KeyModifiers == KeyModifiers.None
            && vm.Selection.Count > 1 && vm.Selection.Contains(entry))
        {
            _deferredSelect = entry;
            e.Handled = true;
        }
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
        if (!_dragging)
        {
            // Plain click (no drag) on an entry of a multi selection: now select only that entry.
            if (_deferredSelect is { } single)
            {
                // After the DataGrid has processed the release itself.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    // Not "SelectedItem = single": it may already be the SelectedItem, then nothing would change.
                    // The list may have been reloaded meanwhile (e.g. after a double-click).
                    if (Vm?.Items.Contains(single) != true) return;
                    Grid.SelectedItems.Clear();
                    Grid.SelectedItems.Add(single);
                });
                _deferredSelect = null;
            }
            return;
        }
        _deferredSelect = null;
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
