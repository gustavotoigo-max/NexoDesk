using System.Windows;
using NexoDesk.App.ViewModels;
using NexoDesk.Ui.Controls;

namespace NexoDesk.App.Views;

public partial class SettingsWindow : NxWindow
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Fecha esta janela antes de perguntar, para a confirmação aparecer sobre a janela principal.</summary>
    private void OnSwitchRole(object sender, RoutedEventArgs e)
    {
        Close();
        Dispatcher.InvokeAsync(() => _vm.SwitchRoleCommand.Execute(null));
    }
}
