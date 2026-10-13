using System.Collections.Specialized;
using System.ComponentModel;
using NexoDesk.Ui.Controls;

namespace NexoDesk.Agent.Views;

/// <summary>Janela de estado do Agent. Fechar apenas esconde: o Agent continua na área de notificação.</summary>
public partial class MainWindow : NxWindow
{
    public MainWindow(AgentController controller)
    {
        InitializeComponent();
        DataContext = controller;
        controller.Log.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                LogScroll.ScrollToEnd();
        };
    }

    /// <summary>Quando true, fechar encerra de verdade (comando "Sair" da bandeja).</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
