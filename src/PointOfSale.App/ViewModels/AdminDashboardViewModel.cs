using System.Collections.ObjectModel;
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
    private readonly MraApiOptions _mra;

    public AdminDashboardViewModel(
        InventoryViewModel inventoryWorkspace,
        UserManagementViewModel userManagement,
        AdminAnalyticsViewModel analytics,
        ILocalInventoryRepository inventory,
        IAuthenticationAuthorizationService auth,
        INavigationService navigation,
        IConfigurationRepository config,
        IOptions<MraApiOptions> mra)
    {
        InventoryWorkspace = inventoryWorkspace;
        UserManagement = userManagement;
        Analytics = analytics;
        _inventory = inventory;
        _auth = auth;
        _navigation = navigation;
        _config = config;
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

    private async Task InitializeAsync()
    {
        await RefreshOverviewAsync().ConfigureAwait(true);
        await LoadFiscalPanelAsync().ConfigureAwait(true);
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
        await _auth.SignOutAsync().ConfigureAwait(true);
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
