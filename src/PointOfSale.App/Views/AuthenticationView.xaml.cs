using System.Windows;
using PointOfSale.App.ViewModels;

namespace PointOfSale.App.Views;

public partial class AuthenticationView
{
    public AuthenticationView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AuthenticationViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        }

        if (e.NewValue is AuthenticationViewModel newVm)
        {
            newVm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AuthenticationViewModel.Password)
            && string.IsNullOrEmpty((sender as AuthenticationViewModel)?.Password))
        {
            PasswordField.Clear();
        }
    }
}
