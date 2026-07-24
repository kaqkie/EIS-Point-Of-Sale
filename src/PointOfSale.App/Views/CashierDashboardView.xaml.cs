using System.Windows.Controls;
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

    private void ProductQuickPick_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var checkout = ViewModel?.Checkout;
        if (checkout is null || !checkout.AddSelectedToCartCommand.CanExecute(null))
        {
            return;
        }

        checkout.AddSelectedToCartCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var checkout = ViewModel?.Checkout;
        if (checkout is null || checkout.IsBusy)
        {
            return;
        }

        // Don't steal digits from search / refund text boxes.
        if (Keyboard.FocusedElement is TextBox)
        {
            HandleFunctionKeysOnly(checkout, e);
            return;
        }

        if (ViewModel?.IsCashRegisterMode == true)
        {
            switch (e.Key)
            {
                case Key.D0:
                case Key.NumPad0:
                    checkout.KeypadPressCommand.Execute("0");
                    e.Handled = true;
                    return;
                case Key.D1:
                case Key.NumPad1:
                    checkout.KeypadPressCommand.Execute("1");
                    e.Handled = true;
                    return;
                case Key.D2:
                case Key.NumPad2:
                    checkout.KeypadPressCommand.Execute("2");
                    e.Handled = true;
                    return;
                case Key.D3:
                case Key.NumPad3:
                    checkout.KeypadPressCommand.Execute("3");
                    e.Handled = true;
                    return;
                case Key.D4:
                case Key.NumPad4:
                    checkout.KeypadPressCommand.Execute("4");
                    e.Handled = true;
                    return;
                case Key.D5:
                case Key.NumPad5:
                    checkout.KeypadPressCommand.Execute("5");
                    e.Handled = true;
                    return;
                case Key.D6:
                case Key.NumPad6:
                    checkout.KeypadPressCommand.Execute("6");
                    e.Handled = true;
                    return;
                case Key.D7:
                case Key.NumPad7:
                    checkout.KeypadPressCommand.Execute("7");
                    e.Handled = true;
                    return;
                case Key.D8:
                case Key.NumPad8:
                    checkout.KeypadPressCommand.Execute("8");
                    e.Handled = true;
                    return;
                case Key.D9:
                case Key.NumPad9:
                    checkout.KeypadPressCommand.Execute("9");
                    e.Handled = true;
                    return;
                case Key.OemPeriod:
                case Key.Decimal:
                    checkout.KeypadPressCommand.Execute(".");
                    e.Handled = true;
                    return;
                case Key.Back:
                    checkout.KeypadBackspaceCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    checkout.KeypadClearCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    checkout.KeypadConfirmTenderCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        HandleFunctionKeysOnly(checkout, e);
    }

    private static void HandleFunctionKeysOnly(CheckoutViewModel checkout, KeyEventArgs e)
    {
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
