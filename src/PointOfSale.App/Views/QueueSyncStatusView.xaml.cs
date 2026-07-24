using System.Windows;
using System.Windows.Controls;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class QueueSyncStatusView
{
    public QueueSyncStatusView()
    {
        InitializeComponent();
        Unloaded += (_, _) =>
        {
            if (DataContext is IDisposable disposable)
            {
                disposable.Dispose();
            }
        };
    }

    /// <summary>
    /// Keeps <see cref="QueueSyncStatusViewModel.SelectedQueueItem"/> aligned with the visually
    /// highlighted DataGrid row so toolbar Print / Retry / Force Sync target that invoice.
    /// </summary>
    private void QueueGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not QueueSyncStatusViewModel vm)
        {
            return;
        }

        if (QueueGrid.SelectedItem is QueueItemViewModel selected)
        {
            if (!ReferenceEquals(vm.SelectedQueueItem, selected))
            {
                vm.SelectedQueueItem = selected;
            }

            return;
        }

        // Do not clear selection during ItemsSource refresh churn (Clear + re-add).
        if (QueueGrid.Items.Count == 0)
        {
            vm.SelectedQueueItem = null;
        }
    }

    /// <summary>
    /// Selecting the row before the command runs so toolbar state and SelectedQueueItem match
    /// the row whose action button was clicked.
    /// </summary>
    private void RowActionButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: QueueItemViewModel row })
        {
            return;
        }

        QueueGrid.SelectedItem = row;
        if (DataContext is QueueSyncStatusViewModel vm)
        {
            vm.SelectedQueueItem = row;
        }
    }
}
