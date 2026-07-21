using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly INavigationService _navigation;

    public LoginViewModel(IAuthenticationAuthorizationService auth, INavigationService navigation)
    {
        _auth = auth;
        _navigation = navigation;
    }

    [ObservableProperty]
    private string _username = string.Empty;

    /// <summary>Bound from PasswordBox code-behind (never persisted).</summary>
    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Sign in to continue retail operations.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isAuthenticated;

    [ObservableProperty]
    private OperatorSession? _currentOperator;

    [ObservableProperty]
    private string? _selectedRole;

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _auth.SignInAsync(Username, Password).ConfigureAwait(true);
            Password = string.Empty;
            if (!result.Success || result.Session is null)
            {
                StatusMessage = result.Error ?? "Sign-in failed.";
                IsAuthenticated = false;
                CurrentOperator = null;
                SelectedRole = null;
                return;
            }

            CurrentOperator = result.Session;
            SelectedRole = result.Session.Role;
            IsAuthenticated = true;
            StatusMessage = $"Welcome, {result.Session.DisplayName}.";
            if (_auth.HasPermission(OperatorPermissions.ExecuteCheckout))
            {
                _navigation.NavigateTo<CheckoutViewModel>();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
