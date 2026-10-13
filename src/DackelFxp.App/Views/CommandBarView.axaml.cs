using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DackelFxp.App.ViewModels;

namespace DackelFxp.App.Views;

public partial class CommandBarView : UserControl
{
    private CommandBarViewModel? _vm;

    public CommandBarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.FocusRequested -= FocusInput;
            _vm = DataContext as CommandBarViewModel;
            if (_vm is not null) _vm.FocusRequested += FocusInput;
        };
        // Up/Down browse the command history.
        Input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_vm is null) return;
            if (e.Key == Key.Up) { _vm.HistoryUp(); e.Handled = true; }
            else if (e.Key == Key.Down) { _vm.HistoryDown(); e.Handled = true; }
            if (e.Handled) Input.CaretIndex = Input.Text?.Length ?? 0;
        }, RoutingStrategies.Tunnel);
    }

    private void FocusInput() => Dispatcher.UIThread.Post(() => Input.Focus(), DispatcherPriority.Background);
}
