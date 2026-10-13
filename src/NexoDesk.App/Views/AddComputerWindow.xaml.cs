using System.ComponentModel;
using NexoDesk.App.ViewModels;
using NexoDesk.Ui.Controls;

namespace NexoDesk.App.Views;

public partial class AddComputerWindow : NxWindow
{
    private readonly AddComputerViewModel _vm;

    public AddComputerWindow(AddComputerViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.CloseRequested += Close;
        Loaded += (_, _) => AddressBox.Focus();
    }

    /// <summary>Fechar no meio do pareamento cancela a tentativa.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_vm.IsBusy)
            _vm.CancelCommand.Execute(null);
    }
}
