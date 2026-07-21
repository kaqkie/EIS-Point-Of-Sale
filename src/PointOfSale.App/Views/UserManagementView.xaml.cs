using System.Windows;
using System.Windows.Controls;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class UserManagementView
{
    public UserManagementView()
    {
        InitializeComponent();
    }

    private void NewPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is UserManagementViewModel vm && sender is PasswordBox box)
        {
            vm.NewPassword = box.Password;
        }
    }

    private void ResetPasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is UserManagementViewModel vm && sender is PasswordBox box)
        {
            vm.ResetPassword = box.Password;
        }
    }
}
