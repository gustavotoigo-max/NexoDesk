using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using NexoDesk.Admin.ViewModels;
using NexoDesk.Ui.Controls;

namespace NexoDesk.Admin.Views;

public partial class MainWindow : NxWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.Log.CollectionChanged += OnLogChanged;
        };
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            LogScroll.ScrollToEnd();
    }

    /// <summary>Fechar com sessão aberta ou conexão em andamento pede confirmação.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (DataContext is not MainViewModel vm || (!vm.IsConnecting && !vm.HasOpenSessions))
            return;

        bool close = NxDialogWindow.Ask(this, new DialogOptions
        {
            Kind = DialogKind.Warning,
            Title = "Encerrar o NexoDesk Admin?",
            Text = vm.IsConnecting
                ? "Há uma conexão em andamento. Ela será cancelada."
                : "Há sessões abertas. Elas serão encerradas e os computadores remotos serão avisados.",
            PrimaryText = "Encerrar",
            SecondaryText = "Continuar usando",
            PrimaryIsDanger = true,
        });

        if (!close)
        {
            e.Cancel = true;
            return;
        }

        vm.CancelConnectCommand.Execute(null);
        foreach (SessionWindow w in Application.Current.Windows.OfType<SessionWindow>().ToList())
            w.Close();
    }
}
