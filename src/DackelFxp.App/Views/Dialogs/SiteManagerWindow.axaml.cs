using Avalonia.Controls;
using DackelFxp.App.ViewModels;

namespace DackelFxp.App.Views.Dialogs;

public partial class SiteManagerWindow : Window
{
    public SiteManagerWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SiteManagerViewModel vm) vm.Close = result => Close(result);
        };
    }
}
