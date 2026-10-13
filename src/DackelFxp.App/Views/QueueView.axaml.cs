using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DackelFxp.App.ViewModels;
using DackelFxp.Core.Queue;

namespace DackelFxp.App.Views;

public partial class QueueView : UserControl
{
    public QueueView()
    {
        InitializeComponent();
        Grid.SelectionChanged += (_, _) =>
        {
            if (DataContext is QueueViewModel vm) vm.Selection = Grid.SelectedItems.OfType<QueueItem>().ToList();
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Delete && DataContext is QueueViewModel vm)
            {
                vm.RemoveSelectedCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }
}
