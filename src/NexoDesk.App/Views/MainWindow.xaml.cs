using System.Windows;
using NexoDesk.App.ViewModels;
using NexoDesk.Ui.Controls;

namespace NexoDesk.App.Views;

/// <summary>Janela principal: a mesma moldura nos dois modos; o conteúdo é a tela ADMIN ou a tela PC REMOTO.</summary>
public partial class MainWindow : NxWindow
{
    private readonly ShellViewModel _shell;

    public MainWindow(ShellViewModel shell)
    {
        InitializeComponent();
        _shell = shell;
        DataContext = shell;
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_shell.Settings) { Owner = this }.ShowDialog();
    }
}
