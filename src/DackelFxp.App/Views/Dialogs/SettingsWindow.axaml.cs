using Avalonia.Controls;
using DackelFxp.App.ViewModels;

namespace DackelFxp.App.Views.Dialogs;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) vm.Close = Close;
        };
    }
}
