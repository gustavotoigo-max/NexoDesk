using System.Collections.Specialized;
using System.Windows.Controls;
using NexoDesk.App.ViewModels;

namespace NexoDesk.App.Views;

public partial class RemoteView : UserControl
{
    public RemoteView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RemoteViewModel vm)
                vm.Log.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add)
                        LogScroll.ScrollToEnd();
                };
        };
    }
}
