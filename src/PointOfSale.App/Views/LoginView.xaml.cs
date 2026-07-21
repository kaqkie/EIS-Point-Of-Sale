using System.Windows;
using System.Windows.Controls;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class LoginView
{
    public LoginView()
    {
        InitializeComponent();
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm && sender is PasswordBox box)
        {
            vm.Password = box.Password;
        }
    }
}
