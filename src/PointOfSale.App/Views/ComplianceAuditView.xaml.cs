using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class ComplianceAuditView
{
    public ComplianceAuditView()
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
