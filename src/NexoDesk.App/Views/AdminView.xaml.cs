using System.Collections.Specialized;
using System.Windows.Controls;
using NexoDesk.App.ViewModels;

namespace NexoDesk.App.Views;

public partial class AdminView : UserControl
{
    public AdminView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AdminViewModel vm)
                vm.Log.CollectionChanged += (_, e) =>
                {
                    if (e.Action == NotifyCollectionChangedAction.Add)
                        LogScroll.ScrollToEnd();
                };
        };
    }
}
