using System.Windows;
using System.Windows.Controls;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class FiscalRolloverView
{
    public FiscalRolloverView()
    {
        InitializeComponent();
    }

    private void SecondarySupervisorPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is FiscalRolloverViewModel vm && sender is PasswordBox box)
        {
            vm.SecondarySupervisorPassword = box.Password;
        }
    }

    private void PrimaryArchivePasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is FiscalRolloverViewModel vm && sender is PasswordBox box)
        {
            vm.PrimaryArchivePassword = box.Password;
        }
    }

    private void SecondaryArchivePasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is FiscalRolloverViewModel vm && sender is PasswordBox box)
        {
            vm.SecondaryArchivePassword = box.Password;
        }
    }
}
