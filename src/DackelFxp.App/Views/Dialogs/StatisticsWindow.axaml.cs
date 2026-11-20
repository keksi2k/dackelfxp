using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DackelFxp.App.Views.Dialogs;

public partial class StatisticsWindow : Window
{
    public StatisticsWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
