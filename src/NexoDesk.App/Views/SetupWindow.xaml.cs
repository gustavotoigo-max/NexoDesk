using System.Windows;
using NexoDesk.App.ViewModels;
using NexoDesk.Ui.Controls;

namespace NexoDesk.App.Views;

public partial class SetupWindow : NxWindow
{
    public SetupWindow(SetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Completed += () =>
        {
            DialogResult = true;
            Close();
        };
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
