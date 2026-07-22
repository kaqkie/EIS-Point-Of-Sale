namespace PointOfSale.App.Views;

public partial class HardwareManagementView
{
    public HardwareManagementView()
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
