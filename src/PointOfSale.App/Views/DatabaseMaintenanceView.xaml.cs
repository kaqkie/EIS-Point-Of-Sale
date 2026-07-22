using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class DatabaseMaintenanceView
{
    public DatabaseMaintenanceView()
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
