using System.ComponentModel;
using NexoDesk.App.ViewModels;
using NexoDesk.Ui.Controls;

namespace NexoDesk.App.Views;

public partial class SessionWindow : NxWindow
{
    private readonly SessionViewModel _vm;

    public SessionWindow(SessionViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.CloseRequested += Close;
    }

    /// <summary>Fechar a janela encerra a sessão de forma ordenada (avisa o Agent).</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel && !_vm.IsEnded)
            _ = _vm.DisconnectAsync();
    }
}
