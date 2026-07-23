using System.Windows.Input;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class CashierDashboardView
{
    public CashierDashboardView()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private CashierDashboardViewModel? ViewModel => DataContext as CashierDashboardViewModel;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var checkout = ViewModel?.Checkout;
        if (checkout is null || checkout.IsBusy)
        {
            return;
        }

        // Parity shortcuts for Cash Register and Full Workspace (bubbled from header focus).
        switch (e.Key)
        {
            case Key.F2:
                checkout.AddSelectedToCartCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F5:
                checkout.TenderExactAmountCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F9:
                if (checkout.ReprintLastReceiptCommand.CanExecute(null))
                {
                    checkout.ReprintLastReceiptCommand.Execute(null);
                }

                e.Handled = true;
                break;
            case Key.F12:
                if (checkout.CompleteSaleCommand.CanExecute(null))
                {
                    checkout.CompleteSaleCommand.Execute(null);
                }

                e.Handled = true;
                break;
        }
    }
}
