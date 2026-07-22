using System.Windows;
using System.Windows.Controls;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class TerminalProvisioningView
{
    public TerminalProvisioningView()
    {
        InitializeComponent();
    }

    private void TerminalActivationCodeBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is TerminalProvisioningViewModel vm && sender is PasswordBox box)
        {
            vm.TerminalActivationCode = box.Password;
        }
    }
}
