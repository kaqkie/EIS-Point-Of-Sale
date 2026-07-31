using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Pricing;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Options;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Admin management console: inventory overview, fiscal VAT/MRA config, users, and analytics hub.
/// </summary>
public partial class AdminDashboardViewModel : ObservableObject
{
    private readonly ILocalInventoryRepository _inventory;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly INavigationService _navigation;
    private readonly IConfigurationRepository _config;
    private readonly ITerminalConnectivityActionsService _connectivityActions;
    private readonly ITerminalFactoryResetService _factoryReset;
    private readonly IFirstRunBootstrapService _firstRun;
    private readonly ITerminalActivationService _activation;
    private readonly MraApiOptions _mra;

    public AdminDashboardViewModel(
        InventoryViewModel inventoryWorkspace,
        UserManagementViewModel userManagement,
        AdminAnalyticsViewModel analytics,
        ILocalInventoryRepository inventory,
        IAuthenticationAuthorizationService auth,
        INavigationService navigation,
        IConfigurationRepository config,
        ITerminalConnectivityActionsService connectivityActions,
        ITerminalFactoryResetService factoryReset,
        IFirstRunBootstrapService firstRun,
        ITerminalActivationService activation,
        IOptions<MraApiOptions> mra)
    {
        InventoryWorkspace = inventoryWorkspace;
        UserManagement = userManagement;
        Analytics = analytics;
        _inventory = inventory;
        _auth = auth;
        _navigation = navigation;
        _config = config;
        _connectivityActions = connectivityActions;
        _factoryReset = factoryReset;
        _firstRun = firstRun;
        _activation = activation;
        _mra = mra.Value;
        LowStockItems = new ObservableCollection<string>();
        SelectedAdminTab = 0;
        StatutoryVatRatePercent = PosTaxCalculator.MalawiStandardVatRatePercent;
        _ = InitializeAsync();
    }

    public InventoryViewModel InventoryWorkspace { get; }
    public UserManagementViewModel UserManagement { get; }
    public AdminAnalyticsViewModel Analytics { get; }
    public ObservableCollection<string> LowStockItems { get; }

    public string[] RoleOptions => OperatorRoles.All;

    [ObservableProperty]
    private int _selectedAdminTab;

    [ObservableProperty]
    private string _statusMessage = "Admin console loading…";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _productCount;

    [ObservableProperty]
    private int _lowStockCount;

    [ObservableProperty]
    private int _operatorCount;

    [ObservableProperty]
    private decimal _statutoryVatRatePercent;

    [ObservableProperty]
    private bool _vatRuleVerified;

    [ObservableProperty]
    private string _mraEnvironment = string.Empty;

    [ObservableProperty]
    private string _mraEndpoint = string.Empty;

    [ObservableProperty]
    private string _mraProductId = string.Empty;

    [ObservableProperty]
    private string _configuredVatDisplay = string.Empty;

    [ObservableProperty]
    private string _selfCurrentPassword = string.Empty;

    [ObservableProperty]
    private string _selfNewPassword = string.Empty;

    [ObservableProperty]
    private string _selfConfirmPassword = string.Empty;

    [ObservableProperty]
    private string _mraPingStatus = "MRA ping: not run yet.";

    [ObservableProperty]
    private string _terminalUpdateStatus = "Terminal update: not checked yet.";

    [ObservableProperty]
    private string _apiSyncStatus = "API sync: not verified yet.";

    private async Task InitializeAsync()
    {
        await RefreshOverviewAsync().ConfigureAwait(true);
        await LoadFiscalPanelAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PingMraAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Pinging MRA EIS…";
            var result = await _connectivityActions.PingMraAsync().ConfigureAwait(true);
            MraPingStatus = result.Message;
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            MraPingStatus = $"MRA ping error: {ex.Message}";
            StatusMessage = MraPingStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CheckTerminalUpdatesAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Checking terminal updates…";
            var result = await _connectivityActions.CheckTerminalUpdatesAsync().ConfigureAwait(true);
            TerminalUpdateStatus = result.Message;
            StatusMessage = result.Message;
        }
        catch (Exception ex)
        {
            TerminalUpdateStatus = $"Update check error: {ex.Message}";
            StatusMessage = TerminalUpdateStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task VerifyAndSyncApisAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Syncing MRA APIs, configs, and EIS inventory…";
            var result = await _connectivityActions.VerifyAndSyncApisAsync().ConfigureAwait(true);
            ApiSyncStatus = result.Message;
            MraPingStatus = result.Ping.Message;

            // Refresh admin inventory workspace + overview counters after product pull.
            if (InventoryWorkspace.RefreshCommand.CanExecute(null))
            {
                await InventoryWorkspace.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
            }

            await RefreshOverviewAsync().ConfigureAwait(true);

            var inventoryBit = result.InventorySynced
                ? $"{result.ProductsSynced} product(s) in local inventory"
                : (result.InventoryRemark ?? "inventory sync incomplete");

            StatusMessage = result.Success
                ? $"API + inventory sync OK — {inventoryBit}. {result.Message}"
                : $"Sync issues — {inventoryBit}. {result.Message}";
        }
        catch (Exception ex)
        {
            ApiSyncStatus = $"API sync error: {ex.Message}";
            StatusMessage = ApiSyncStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshOverviewAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.AccessAdminAnalytics);
            var items = await _inventory.GetAllAsync().ConfigureAwait(true);
            ProductCount = items.Count;
            LowStockItems.Clear();
            foreach (var item in items.Where(i => i.MinReorderQty > 0 && i.StockQuantity <= i.MinReorderQty).Take(12))
            {
                LowStockItems.Add($"{item.ProductCode} · {item.Name} ({item.StockQuantity:N0})");
            }

            LowStockCount = LowStockItems.Count;
            var operators = await _auth.GetOperatorsAsync().ConfigureAwait(true);
            OperatorCount = operators.Count;
            StatusMessage = $"Admin overview · {ProductCount} SKUs · {OperatorCount} operators · VAT {StatutoryVatRatePercent:N1}%";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Admin overview failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadFiscalPanelAsync()
    {
        try
        {
            MraEnvironment = _mra.Environment;
            MraEndpoint = _mra.ResolveBaseUrl();
            MraProductId = _mra.ProductId;
            var stored = await _config.GetJsonAsync("Fiscal.StandardVatRatePercent").ConfigureAwait(true);
            if (decimal.TryParse(stored?.Trim('"'), out var rate))
            {
                ConfiguredVatDisplay = $"{rate:N1}%";
                VatRuleVerified = rate == PosTaxCalculator.MalawiStandardVatRatePercent;
            }
            else
            {
                ConfiguredVatDisplay = $"{PosTaxCalculator.MalawiStandardVatRatePercent:N1}% (code default)";
                VatRuleVerified = true;
            }

            StatutoryVatRatePercent = PosTaxCalculator.MalawiStandardVatRatePercent;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ResetTerminalFactoryAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ProvisionTerminal);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        if (!OperatorWorkspace.IsAdminConsoleRole(_auth.CurrentOperator?.Role))
        {
            StatusMessage = "Terminal reset requires Store Manager or Administrator.";
            return;
        }

        var first = MessageBox.Show(
            "This will ERASE this terminal's local data:\n\n" +
            "• All receipts / offline invoice queue\n" +
            "• All local products / inventory\n" +
            "• MRA activation, JWT, site & taxpayer caches\n" +
            "• License / first-run registry mirrors\n\n" +
            "Operators are kept so you can sign in again.\n" +
            "You must complete first-run activation with a new TAC.\n\n" +
            "Continue?",
            "Terminal reset — confirm",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (first != MessageBoxResult.Yes)
        {
            StatusMessage = "Terminal reset cancelled.";
            return;
        }

        var second = MessageBox.Show(
            "FINAL CONFIRMATION\n\nErase receipts, products, and terminal identity now?\nThis cannot be undone from the POS.",
            "Terminal reset — final confirm",
            MessageBoxButton.YesNo,
            MessageBoxImage.Stop,
            MessageBoxResult.No);

        if (second != MessageBoxResult.Yes)
        {
            StatusMessage = "Terminal reset cancelled.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Resetting terminal — clearing receipts, products, and identity…";
            var result = await _factoryReset.ResetAsync().ConfigureAwait(true);
            StatusMessage = result.Message;

            if (!result.Success)
            {
                return;
            }

            ProductCount = 0;
            LowStockCount = 0;
            LowStockItems.Clear();

            await _firstRun.RefreshStatusAsync().ConfigureAwait(true);
            await _activation.GetStatusAsync().ConfigureAwait(true);
            await _auth.SignOutAsync().ConfigureAwait(true);

            MessageBox.Show(
                result.Message + "\n\nYou will return to sign-in / first-run setup.",
                "Terminal reset complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Terminal reset error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task EnforceStatutoryVatAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _auth.EnsurePermission(OperatorPermissions.AccessCompliance);
            await _config.UpsertJsonAsync(
                    "Fiscal.StandardVatRatePercent",
                    PosTaxCalculator.MalawiStandardVatRatePercent.ToString("0.0"))
                .ConfigureAwait(true);
            await _config.UpsertJsonAsync("Fiscal.VatRuleSource", "PosTaxCalculator.MalawiStandardVatRatePercent")
                .ConfigureAwait(true);
            VatRuleVerified = true;
            ConfiguredVatDisplay = $"{PosTaxCalculator.MalawiStandardVatRatePercent:N1}%";
            StatusMessage = "Statutory 17.5% VAT rule written to Configurations and verified.";
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

    [RelayCommand]
    private async Task PersistMraEndpointNotesAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.AccessCompliance);
            await _config.UpsertJsonAsync("Mra.AdminConsole.EndpointSnapshot", MraEndpoint).ConfigureAwait(true);
            await _config.UpsertJsonAsync("Mra.AdminConsole.EnvironmentSnapshot", MraEnvironment).ConfigureAwait(true);
            StatusMessage = "MRA endpoint snapshot saved for audit (runtime URLs still come from appsettings).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ChangeOwnPasswordAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (!string.Equals(SelfNewPassword, SelfConfirmPassword, StringComparison.Ordinal))
            {
                StatusMessage = "New password and confirmation do not match.";
                return;
            }

            var result = await _auth.ChangeOwnPasswordAsync(SelfCurrentPassword, SelfNewPassword)
                .ConfigureAwait(true);
            SelfCurrentPassword = string.Empty;
            SelfNewPassword = string.Empty;
            SelfConfirmPassword = string.Empty;
            StatusMessage = result.Success
                ? "Your password was updated."
                : result.Error ?? "Password change failed.";
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

    [RelayCommand]
    private async Task SignOutAsync()
    {
        // Prefer shell Sign out (sidebar / header). Kept for programmatic session end.
        SelfCurrentPassword = string.Empty;
        SelfNewPassword = string.Empty;
        SelfConfirmPassword = string.Empty;
        await _auth.SignOutAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenCashierRegister()
    {
        if (!_auth.HasPermission(OperatorPermissions.ExecuteCheckout))
        {
            StatusMessage = "Cashier Register requires Execute Checkout permission for this operator.";
            return;
        }

        _navigation.NavigateTo<CashierDashboardViewModel>();
        if (_navigation.CurrentViewModel is CashierDashboardViewModel cashier)
        {
            cashier.ShowCashRegisterMode(enabled: true);
        }

        StatusMessage = "Opened shared POS Terminal — same cart, keypad tender, and Paid checkout for Admin and Cashier.";
    }

    [RelayCommand]
    private void OpenInventory() => _navigation.NavigateTo<InventoryViewModel>();

    [RelayCommand]
    private void OpenStockAlerts() => _navigation.NavigateTo<InventoryAlertsViewModel>();

    [RelayCommand]
    private void OpenDiscounts() => _navigation.NavigateTo<DiscountManagementViewModel>();

    [RelayCommand]
    private void OpenUsers() => _navigation.NavigateTo<UserManagementViewModel>();

    [RelayCommand]
    private void OpenAnalytics() => _navigation.NavigateTo<AdminAnalyticsViewModel>();

    [RelayCommand]
    private void OpenComplianceAudit() => _navigation.NavigateTo<ComplianceAuditViewModel>();

    [RelayCommand]
    private void OpenHardware() => _navigation.NavigateTo<HardwareManagementViewModel>();

    [RelayCommand]
    private async Task RefreshUsersAsync() => await UserManagement.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);

    [RelayCommand]
    private async Task RefreshAnalyticsAsync() => await Analytics.RefreshCommand.ExecuteAsync(null).ConfigureAwait(true);
}
