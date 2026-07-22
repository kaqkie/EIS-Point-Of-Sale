using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly IConnectionStatusService _connectionStatusService;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly ITelemetryDiagnosticService _telemetry;
    private readonly ITerminalActivationService _activation;

    public MainViewModel(
        INavigationService navigationService,
        IConnectionStatusService connectionStatusService,
        IAuthenticationAuthorizationService auth,
        ITelemetryDiagnosticService telemetry,
        ITerminalActivationService activation,
        AuthenticationViewModel authentication,
        ActivationViewModel activationViewModel)
    {
        _navigationService = navigationService;
        _connectionStatusService = connectionStatusService;
        _auth = auth;
        _telemetry = telemetry;
        _activation = activation;
        Authentication = authentication;
        Activation = activationViewModel;

        _navigationService.CurrentViewModelChanged += (_, _) => CurrentViewModel = _navigationService.CurrentViewModel;
        _connectionStatusService.StatusChanged += (_, _) => RefreshConnectionState();
        _activation.ActivationStatusChanged += (_, _) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                RefreshActivationState();
            }
            else
            {
                dispatcher.Invoke(RefreshActivationState);
            }
        };
        _telemetry.HealthChanged += (_, snapshot) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                ApplyHealthSnapshot(snapshot);
            }
            else
            {
                dispatcher.Invoke(() => ApplyHealthSnapshot(snapshot));
            }
        };
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
        RefreshActivationState();
        RefreshAuthState();
        ApplyHealthSnapshot(_telemetry.LatestSnapshot);
        _ = InitializeLicenseAsync();
    }

    public AuthenticationViewModel Authentication { get; }
    public ActivationViewModel Activation { get; }

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
    private bool _showActivationOverlay = true;

    [ObservableProperty]
    private bool _isTerminalActivated;

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

    [ObservableProperty]
    private bool _canLabels;

    [ObservableProperty]
    private bool _canStockAlerts;

    [ObservableProperty]
    private bool _canPurchaseOrders;

    [ObservableProperty]
    private bool _canGoodsReceipt;

    [ObservableProperty]
    private bool _canSupplierRecon;

    [ObservableProperty]
    private bool _canSystemDiagnostics;

    [ObservableProperty]
    private bool _canEndOfDay;

    [ObservableProperty]
    private bool _canFiscalRollover;

    [ObservableProperty]
    private bool _canProvisionTerminal;

    [ObservableProperty]
    private bool _canRunIntegrationTests;

    [ObservableProperty]
    private bool _canEnterpriseMaintenance;

    [ObservableProperty]
    private bool _canDatabaseMaintenance;

    [ObservableProperty]
    private bool _canHardware;

    [ObservableProperty]
    private bool _isCashierShell;

    [ObservableProperty]
    private bool _isAdminShell;

    [ObservableProperty]
    private bool _hasHealthWarning;

    [ObservableProperty]
    private string _healthWarningText = string.Empty;

    [RelayCommand(CanExecute = nameof(CanCheckout))]
    private void NavigateCheckout() => _navigationService.NavigateTo<CheckoutViewModel>();

    [RelayCommand(CanExecute = nameof(CanInventory))]
    private void NavigateInventory() => _navigationService.NavigateTo<InventoryViewModel>();

    [RelayCommand(CanExecute = nameof(CanQueue))]
    private void NavigateQueue() => _navigationService.NavigateTo<QueueSyncStatusViewModel>();

    [RelayCommand(CanExecute = nameof(CanCompliance))]
    private void NavigateCompliance() => _navigationService.NavigateTo<ComplianceExportViewModel>();

    [RelayCommand(CanExecute = nameof(CanCompliance))]
    private void NavigateComplianceAudit() => _navigationService.NavigateTo<ComplianceAuditViewModel>();

    [RelayCommand(CanExecute = nameof(CanAnalytics))]
    private void NavigateAnalytics() => _navigationService.NavigateTo<AdminAnalyticsViewModel>();

    [RelayCommand(CanExecute = nameof(CanHeadOffice))]
    private void NavigateHeadOffice() => _navigationService.NavigateTo<HeadOfficeSyncViewModel>();

    [RelayCommand(CanExecute = nameof(CanBackup))]
    private void NavigateBackup() => _navigationService.NavigateTo<DatabaseBackupViewModel>();

    [RelayCommand(CanExecute = nameof(CanManageUsers))]
    private void NavigateUsers() => _navigationService.NavigateTo<UserManagementViewModel>();

    [RelayCommand(CanExecute = nameof(CanLoyalty))]
    private void NavigateLoyalty() => _navigationService.NavigateTo<CustomerLoyaltyViewModel>();

    [RelayCommand(CanExecute = nameof(CanDiscounts))]
    private void NavigateDiscounts() => _navigationService.NavigateTo<DiscountManagementViewModel>();

    [RelayCommand(CanExecute = nameof(CanLabels))]
    private void NavigateLabels() => _navigationService.NavigateTo<BarcodePrintingViewModel>();

    [RelayCommand(CanExecute = nameof(CanStockAlerts))]
    private void NavigateStockAlerts() => _navigationService.NavigateTo<InventoryAlertsViewModel>();

    [RelayCommand(CanExecute = nameof(CanPurchaseOrders))]
    private void NavigatePurchaseOrders() => _navigationService.NavigateTo<PurchaseOrderManagementViewModel>();

    [RelayCommand(CanExecute = nameof(CanGoodsReceipt))]
    private void NavigateGoodsReceipt() => _navigationService.NavigateTo<GoodsReceiptViewModel>();

    [RelayCommand(CanExecute = nameof(CanSupplierRecon))]
    private void NavigateSupplierRecon() => _navigationService.NavigateTo<SupplierInvoiceReconciliationViewModel>();

    [RelayCommand(CanExecute = nameof(CanSystemDiagnostics))]
    private void NavigateSystemDiagnostics() => _navigationService.NavigateTo<SystemDiagnosticsViewModel>();

    [RelayCommand(CanExecute = nameof(CanEndOfDay))]
    private void NavigateEndOfDay() => _navigationService.NavigateTo<EndofDaySummaryViewModel>();

    [RelayCommand(CanExecute = nameof(CanFiscalRollover))]
    private void NavigateFiscalRollover() => _navigationService.NavigateTo<FiscalRolloverViewModel>();

    [RelayCommand(CanExecute = nameof(CanProvisionTerminal))]
    private void NavigateTerminalProvisioning() => _navigationService.NavigateTo<TerminalProvisioningViewModel>();

    [RelayCommand(CanExecute = nameof(CanRunIntegrationTests))]
    private void NavigateTestRunner() => _navigationService.NavigateTo<TestRunnerDashboardViewModel>();

    [RelayCommand(CanExecute = nameof(CanEnterpriseMaintenance))]
    private void NavigateEnterpriseMaintenance() => _navigationService.NavigateTo<EnterpriseMaintenanceViewModel>();

    [RelayCommand(CanExecute = nameof(CanDatabaseMaintenance))]
    private void NavigateDatabaseMaintenance() => _navigationService.NavigateTo<DatabaseMaintenanceViewModel>();

    [RelayCommand(CanExecute = nameof(CanHardware))]
    private void NavigateHardware() => _navigationService.NavigateTo<HardwareManagementViewModel>();

    [RelayCommand(CanExecute = nameof(IsCashierShell))]
    private void NavigateCashierHome() => _navigationService.NavigateTo<CashierDashboardViewModel>();

    [RelayCommand(CanExecute = nameof(IsAdminShell))]
    private void NavigateAdminHome() => _navigationService.NavigateTo<AdminDashboardViewModel>();

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

    private void ApplyHealthSnapshot(SystemHealthSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            HasHealthWarning = false;
            HealthWarningText = string.Empty;
            return;
        }

        HasHealthWarning = !snapshot.OverallHealthy;
        HealthWarningText = snapshot.OverallHealthy
            ? string.Empty
            : $"System health warning: {snapshot.Summary}";
    }

    private async Task InitializeLicenseAsync()
    {
        try
        {
            await _activation.GetStatusAsync().ConfigureAwait(true);
        }
        catch
        {
            // Status text already reflects failures; overlay remains until activated.
        }

        RefreshActivationState();
        RefreshAuthState();
    }

    private void RefreshActivationState()
    {
        IsTerminalActivated = _activation.IsActivated;
        ShowActivationOverlay = !_activation.IsActivated;
    }

    private void RefreshAuthState()
    {
        IsAuthenticated = _auth.IsAuthenticated;
        ShowLoginOverlay = _activation.IsActivated && !_auth.IsAuthenticated;
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
        CanLabels = _auth.HasPermission(OperatorPermissions.PrintProductLabels);
        CanStockAlerts = _auth.HasPermission(OperatorPermissions.ViewInventoryAlerts);
        CanPurchaseOrders = _auth.HasPermission(OperatorPermissions.ManagePurchaseOrders);
        CanGoodsReceipt = _auth.HasPermission(OperatorPermissions.ProcessGoodsReceipt);
        CanSupplierRecon = _auth.HasPermission(OperatorPermissions.ReconcileSupplierInvoices);
        CanSystemDiagnostics = _auth.HasPermission(OperatorPermissions.ViewSystemDiagnostics);
        CanEndOfDay = _auth.HasPermission(OperatorPermissions.CloseFinancialDay);
        CanFiscalRollover = _auth.HasPermission(OperatorPermissions.ExecuteFiscalYearRollover);
        CanProvisionTerminal = _auth.HasPermission(OperatorPermissions.ProvisionTerminal);
        CanRunIntegrationTests = _auth.HasPermission(OperatorPermissions.RunIntegrationTests);
        CanEnterpriseMaintenance = _auth.HasPermission(OperatorPermissions.ExecuteEnterpriseMaintenance);
        CanDatabaseMaintenance = _auth.HasPermission(OperatorPermissions.ManageDatabaseMaintenance);
        CanHardware = _auth.HasPermission(OperatorPermissions.ManageHardwarePeripherals);

        var role = session?.Role;
        IsCashierShell = IsAuthenticated && OperatorWorkspace.IsCashierWorkspaceRole(role);
        IsAdminShell = IsAuthenticated && OperatorWorkspace.IsAdminConsoleRole(role);

        NavigateCheckoutCommand.NotifyCanExecuteChanged();
        NavigateInventoryCommand.NotifyCanExecuteChanged();
        NavigateQueueCommand.NotifyCanExecuteChanged();
        NavigateComplianceCommand.NotifyCanExecuteChanged();
        NavigateComplianceAuditCommand.NotifyCanExecuteChanged();
        NavigateAnalyticsCommand.NotifyCanExecuteChanged();
        NavigateHeadOfficeCommand.NotifyCanExecuteChanged();
        NavigateBackupCommand.NotifyCanExecuteChanged();
        NavigateUsersCommand.NotifyCanExecuteChanged();
        NavigateLoyaltyCommand.NotifyCanExecuteChanged();
        NavigateDiscountsCommand.NotifyCanExecuteChanged();
        NavigateLabelsCommand.NotifyCanExecuteChanged();
        NavigateStockAlertsCommand.NotifyCanExecuteChanged();
        NavigatePurchaseOrdersCommand.NotifyCanExecuteChanged();
        NavigateGoodsReceiptCommand.NotifyCanExecuteChanged();
        NavigateSupplierReconCommand.NotifyCanExecuteChanged();
        NavigateSystemDiagnosticsCommand.NotifyCanExecuteChanged();
        NavigateEndOfDayCommand.NotifyCanExecuteChanged();
        NavigateFiscalRolloverCommand.NotifyCanExecuteChanged();
        NavigateTerminalProvisioningCommand.NotifyCanExecuteChanged();
        NavigateTestRunnerCommand.NotifyCanExecuteChanged();
        NavigateEnterpriseMaintenanceCommand.NotifyCanExecuteChanged();
        NavigateDatabaseMaintenanceCommand.NotifyCanExecuteChanged();
        NavigateHardwareCommand.NotifyCanExecuteChanged();
        NavigateCashierHomeCommand.NotifyCanExecuteChanged();
        NavigateAdminHomeCommand.NotifyCanExecuteChanged();
    }
}
