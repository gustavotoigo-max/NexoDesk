using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using NexoDesk.Admin.ViewModels;
using NexoDesk.Admin.Views;
using NexoDesk.Ui;

namespace NexoDesk.Admin;

public partial class App : Application
{
    public static string Version { get; } =
        (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        ThemeManager.Install(this);

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();
        vm.Initialize();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show(
            $"Ocorreu um erro inesperado e a ação foi interrompida.\n\n{e.Exception.GetType().Name}: {e.Exception.Message}",
            "NexoDesk Admin", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
