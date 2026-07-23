using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Light-themed sign-in with role-based routing into Cashier or Admin workspaces.
/// Credentials validate against dbo.Operators (SQL Express operator directory).
/// </summary>
public partial class AuthenticationViewModel : ObservableObject
{
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly INavigationService _navigation;

    public AuthenticationViewModel(
        IAuthenticationAuthorizationService auth,
        INavigationService navigation)
    {
        _auth = auth;
        _navigation = navigation;
    }

    [ObservableProperty]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Sign in with your Albert Retail operator credentials.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isAuthenticated;

    [ObservableProperty]
    private OperatorSession? _currentOperator;

    [ObservableProperty]
    private string? _routedWorkspace;

    public void ResetForSignIn()
    {
        Password = string.Empty;
        IsBusy = false;
        IsAuthenticated = false;
        CurrentOperator = null;
        RoutedWorkspace = null;
        StatusMessage = "Sign in with your Albert Retail operator credentials.";
    }

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
                RoutedWorkspace = null;
                return;
            }

            CurrentOperator = result.Session;
            IsAuthenticated = true;
            RoutedWorkspace = OperatorWorkspace.ResolveShell(result.Session.Role);
            StatusMessage = $"Welcome, {result.Session.DisplayName} · {RoutedWorkspace} workspace";

            if (OperatorWorkspace.IsAdminConsoleRole(result.Session.Role))
            {
                _navigation.NavigateTo<AdminDashboardViewModel>();
            }
            else
            {
                _navigation.NavigateTo<CashierDashboardViewModel>();
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
