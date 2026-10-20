using Avalonia.Controls;
using DackelFxp.App.ViewModels;

namespace DackelFxp.App.Views;

public partial class MainWindow : Window
{
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        LeftPane.Activated += () => (DataContext as MainWindowViewModel)?.Activate(((MainWindowViewModel)DataContext!).Left);
        RightPane.Activated += () => (DataContext as MainWindowViewModel)?.Activate(((MainWindowViewModel)DataContext!).Right);

        Opened += async (_, _) =>
        {
            if (_initialized || DataContext is not MainWindowViewModel vm) return;
            _initialized = true;
            vm.CloseRequested += Close;
            var s = App.Services.Settings.Current;
            Width = s.WindowWidth;
            Height = s.WindowHeight;
            if (s.WindowMaximized) WindowState = WindowState.Maximized;
            await vm.InitializeAsync();
        };

        Closing += (_, _) =>
        {
            LeftPane.StoreColumnLayout();
            RightPane.StoreColumnLayout();
            var s = App.Services.Settings.Current;
            s.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                s.WindowWidth = Width;
                s.WindowHeight = Height;
            }
        };
    }
}
