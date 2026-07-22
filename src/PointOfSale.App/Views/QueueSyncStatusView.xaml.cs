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
}
