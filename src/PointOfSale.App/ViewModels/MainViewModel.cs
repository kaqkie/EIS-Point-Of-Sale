using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly IConnectionStatusService _connectionStatusService;
    private readonly IAuthenticationAuthorizationService _auth;

    public MainViewModel(
        INavigationService navigationService,
        IConnectionStatusService connectionStatusService,
        IAuthenticationAuthorizationService auth,
        LoginViewModel loginViewModel)
    {
        _navigationService = navigationService;
        _connectionStatusService = connectionStatusService;
        _auth = auth;
        Login = loginViewModel;

        _navigationService.CurrentViewModelChanged += (_, _) => CurrentViewModel = _navigationService.CurrentViewModel;
        _connectionStatusService.StatusChanged += (_, _) => RefreshConnectionState();
        _auth.SessionChanged += (_, _) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                RefreshAuthState();
            }
            else
            {
                dispatcher.Invoke(RefreshAuthState);
            }
        };

        RefreshConnectionState();
        RefreshAuthState();
    }

    public LoginViewModel Login { get; }

    [ObservableProperty]
    private object? _currentViewModel;

    [ObservableProperty]
    private bool _isOnline;

    [ObservableProperty]
    private bool _isMraReachable;

    [ObservableProperty]
    private string _connectionStatusText = string.Empty;

    [ObservableProperty]
    private bool _isDrawerOpen = true;

    [ObservableProperty]
    private bool _isAuthenticated;

    [ObservableProperty]
    private bool _showLoginOverlay = true;

    [ObservableProperty]
    private string _operatorDisplay = "Not signed in";

    [ObservableProperty]
    private bool _canCheckout;

    [ObservableProperty]
    private bool _canInventory;

    [ObservableProperty]
    private bool _canQueue;

    [ObservableProperty]
    private bool _canCompliance;

    [ObservableProperty]
    private bool _canAnalytics;

    [ObservableProperty]
    private bool _canHeadOffice;

    [ObservableProperty]
    private bool _canBackup;

    [ObservableProperty]
    private bool _canManageUsers;

    [ObservableProperty]
    private bool _canLoyalty;

    [ObservableProperty]
    private bool _canDiscounts;

    [RelayCommand(CanExecute = nameof(CanCheckout))]
    private void NavigateCheckout() => _navigationService.NavigateTo<CheckoutViewModel>();

    [RelayCommand(CanExecute = nameof(CanInventory))]
    private void NavigateInventory() => _navigationService.NavigateTo<InventoryViewModel>();

    [RelayCommand(CanExecute = nameof(CanQueue))]
    private void NavigateQueue() => _navigationService.NavigateTo<QueueSyncStatusViewModel>();

    [RelayCommand(CanExecute = nameof(CanCompliance))]
    private void NavigateCompliance() => _navigationService.NavigateTo<ComplianceExportViewModel>();

    [RelayCommand(CanExecute = nameof(CanAnalytics))]
    private void NavigateAnalytics() => _navigationService.NavigateTo<AdminAnalyticsViewModel>();

    [RelayCommand(CanExecute = nameof(CanHeadOffice))]
    private void NavigateHeadOffice() => _navigationService.NavigateTo<HeadOfficeSyncViewModel>();

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private void NavigateBackup() => _navigationService.NavigateTo<BackupRecoveryViewModel>();

    [RelayCommand(CanExecute = nameof(CanManageUsers))]
    private void NavigateUsers() => _navigationService.NavigateTo<UserManagementViewModel>();

    [RelayCommand(CanExecute = nameof(CanLoyalty))]
    private void NavigateLoyalty() => _navigationService.NavigateTo<CustomerLoyaltyViewModel>();

    [RelayCommand(CanExecute = nameof(CanDiscounts))]
    private void NavigateDiscounts() => _navigationService.NavigateTo<DiscountManagementViewModel>();

    [RelayCommand]
    private void ToggleDrawer() => IsDrawerOpen = !IsDrawerOpen;

    [RelayCommand]
    private async Task RefreshConnectionAsync() =>
        await _connectionStatusService.RefreshAsync().ConfigureAwait(true);

    [RelayCommand]
    private async Task SignOutAsync()
    {
        await _auth.SignOutAsync().ConfigureAwait(true);
        CurrentViewModel = null;
        RefreshAuthState();
    }

    private void RefreshConnectionState()
    {
        IsOnline = _connectionStatusService.IsOnline;
        IsMraReachable = _connectionStatusService.IsMraReachable;
        ConnectionStatusText = _connectionStatusService.StatusText;
    }

    private void RefreshAuthState()
    {
        IsAuthenticated = _auth.IsAuthenticated;
        ShowLoginOverlay = !_auth.IsAuthenticated;
        var session = _auth.CurrentOperator;
        OperatorDisplay = session is null
            ? "Not signed in"
            : $"{session.DisplayName} · {session.Role}";

        CanCheckout = _auth.HasPermission(OperatorPermissions.ExecuteCheckout);
        CanInventory = _auth.HasPermission(OperatorPermissions.ManageInventory);
        CanQueue = _auth.HasPermission(OperatorPermissions.ManageQueueSync);
        CanCompliance = _auth.HasPermission(OperatorPermissions.AccessCompliance);
        CanAnalytics = _auth.HasPermission(OperatorPermissions.AccessAdminAnalytics);
        CanHeadOffice = _auth.HasPermission(OperatorPermissions.AccessHeadOffice);
        CanBackup = _auth.HasPermission(OperatorPermissions.TriggerBackup);
        CanManageUsers = _auth.HasPermission(OperatorPermissions.ManageUsers);
        CanLoyalty = _auth.HasPermission(OperatorPermissions.LookupLoyaltyCustomer);
        CanDiscounts = _auth.HasPermission(OperatorPermissions.ManageLoyaltyPrograms);

        NavigateCheckoutCommand.NotifyCanExecuteChanged();
        NavigateInventoryCommand.NotifyCanExecuteChanged();
        NavigateQueueCommand.NotifyCanExecuteChanged();
        NavigateComplianceCommand.NotifyCanExecuteChanged();
        NavigateAnalyticsCommand.NotifyCanExecuteChanged();
        NavigateHeadOfficeCommand.NotifyCanExecuteChanged();
        NavigateBackupCommand.NotifyCanExecuteChanged();
        NavigateUsersCommand.NotifyCanExecuteChanged();
        NavigateLoyaltyCommand.NotifyCanExecuteChanged();
        NavigateDiscountsCommand.NotifyCanExecuteChanged();
    }
}
