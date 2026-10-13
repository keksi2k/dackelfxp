using Avalonia.Controls;
using DackelFxp.App.ViewModels;

namespace DackelFxp.App.Views.Dialogs;

public partial class QuickConnectWindow : Window
{
    public QuickConnectWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is QuickConnectViewModel vm) vm.Close = site => Close(site);
        };
        Opened += (_, _) => HostBox.Focus();
    }
}
