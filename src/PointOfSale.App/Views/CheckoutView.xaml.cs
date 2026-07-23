using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class CheckoutView
{
    public CheckoutView()
    {
        InitializeComponent();
    }

    private CheckoutViewModel? ViewModel => DataContext as CheckoutViewModel;

    private void CheckoutView_OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus();
        Keyboard.Focus(SearchBox);
    }

    private void CheckoutView_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy)
        {
            return;
        }

        // Ensure shortcuts work even when a child TextBox has focus.
        switch (e.Key)
        {
            case Key.F2:
                ViewModel.AddSelectedToCartCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F5:
                ViewModel.TenderExactAmountCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F8:
                ViewModel.OpenQueueStatusCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F9:
                if (ViewModel.ReprintLastReceiptCommand.CanExecute(null))
                {
                    ViewModel.ReprintLastReceiptCommand.Execute(null);
                }

                e.Handled = true;
                break;
            case Key.F12:
                if (ViewModel.CompleteSaleCommand.CanExecute(null))
                {
                    ViewModel.CompleteSaleCommand.Execute(null);
                }

                e.Handled = true;
                break;
            case Key.Enter when ReferenceEquals(Keyboard.FocusedElement, SearchBox)
                               || IsFocusWithinProductList():
                ViewModel.AddSelectedToCartCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private bool IsFocusWithinProductList()
    {
        if (Keyboard.FocusedElement is DependencyObject focused)
        {
            return ReferenceEquals(focused, ProductList) || IsDescendantOf(focused, ProductList);
        }

        return false;
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, ancestor))
            {
                return true;
            }

            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private void ProductList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.AddSelectedToCartCommand.Execute(null);
    }
}
