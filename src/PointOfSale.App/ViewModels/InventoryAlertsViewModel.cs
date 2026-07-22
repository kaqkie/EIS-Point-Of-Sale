using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.ViewModels;

public partial class InventoryAlertsViewModel : ObservableObject
{
    private readonly IInventoryAlertService _alertService;
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly IInventorySupplierRepository _supplierRepository;
    private readonly INavigationService _navigationService;
    private readonly IAuthenticationAuthorizationService _auth;
    private IReadOnlyList<InventoryStockAlert> _allOpen = Array.Empty<InventoryStockAlert>();

    public InventoryAlertsViewModel(
        IInventoryAlertService alertService,
        ILocalInventoryRepository inventoryRepository,
        IInventorySupplierRepository supplierRepository,
        INavigationService navigationService,
        IAuthenticationAuthorizationService auth)
    {
        _alertService = alertService;
        _inventoryRepository = inventoryRepository;
        _supplierRepository = supplierRepository;
        _navigationService = navigationService;
        _auth = auth;
        LowStockItemsList = new ObservableCollection<InventoryStockAlert>();
        SupplierFilters = new ObservableCollection<string> { "All" };
        AlertTypeFilters = new ObservableCollection<string> { "All", "LowStock", "Stockout", "Overstock", "FastMoving" };
        SelectedSupplierFilter = "All";
        SelectedAlertTypeFilter = "All";
        _ = InitializeAsync();
    }

    public ObservableCollection<InventoryStockAlert> LowStockItemsList { get; }
    public ObservableCollection<string> SupplierFilters { get; }
    public ObservableCollection<string> AlertTypeFilters { get; }

    [ObservableProperty]
    private string _selectedSupplierFilter = "All";

    [ObservableProperty]
    private string _selectedAlertTypeFilter = "All";

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private InventoryStockAlert? _selectedAlert;

    [ObservableProperty]
    private int _lowStockCount;

    [ObservableProperty]
    private int _stockoutCount;

    [ObservableProperty]
    private int _fastMovingCount;

    [ObservableProperty]
    private int _overstockCount;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = "Stock alerts refresh automatically in the background.";

    [ObservableProperty]
    private string _editProductCode = string.Empty;

    [ObservableProperty]
    private decimal _editMinReorderQty;

    [ObservableProperty]
    private decimal _editMaxStockCapacity;

    [ObservableProperty]
    private string _editSupplierCode = string.Empty;

    [ObservableProperty]
    private string _editSupplierName = string.Empty;

    partial void OnSelectedSupplierFilterChanged(string value) => ApplyFilters();
    partial void OnSelectedAlertTypeFilterChanged(string value) => ApplyFilters();
    partial void OnSearchQueryChanged(string value) => ApplyFilters();

    partial void OnSelectedAlertChanged(InventoryStockAlert? value)
    {
        if (value is null)
        {
            return;
        }

        EditProductCode = value.ProductCode;
        EditMinReorderQty = value.ThresholdQty;
        EditSupplierCode = value.SupplierCode ?? string.Empty;
        _ = LoadProductSettingsAsync(value.ProductCode);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsScanning)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ViewInventoryAlerts);
            IsScanning = true;
            var result = await _alertService.ScanAsync().ConfigureAwait(true);
            _allOpen = result.OpenAlerts;
            LowStockCount = result.LowStockCount;
            StockoutCount = result.StockoutCount;
            FastMovingCount = result.FastMovingCount;
            OverstockCount = result.OverstockCount;
            RebuildSupplierFilters();
            ApplyFilters();
            StatusMessage =
                $"Scan complete — {result.ScannedProducts} products, {result.OpenAlerts.Count} open alert(s) at {result.ScannedAtUtc:HH:mm:ss} UTC.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task AcknowledgeSelectedAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ViewInventoryAlerts);
            if (SelectedAlert is null)
            {
                StatusMessage = "Select an alert to acknowledge.";
                return;
            }

            await _alertService.AcknowledgeAsync(SelectedAlert.AlertId).ConfigureAwait(true);
            StatusMessage = $"Acknowledged alert #{SelectedAlert.AlertId}.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task AcknowledgeAllAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ViewInventoryAlerts);
            await _alertService.AcknowledgeAllAsync().ConfigureAwait(true);
            StatusMessage = "All open alerts acknowledged.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveThresholdsAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManagePurchaseOrders);
            if (string.IsNullOrWhiteSpace(EditProductCode))
            {
                StatusMessage = "Select a product alert first.";
                return;
            }

            await _inventoryRepository.UpdateReorderSettingsAsync(
                    EditProductCode.Trim(),
                    EditMinReorderQty,
                    EditMaxStockCapacity,
                    string.IsNullOrWhiteSpace(EditSupplierCode) ? null : EditSupplierCode.Trim(),
                    string.IsNullOrWhiteSpace(EditSupplierName) ? null : EditSupplierName.Trim())
                .ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(EditSupplierCode))
            {
                await _supplierRepository.UpsertAsync(
                        new InventorySupplier
                        {
                            SupplierCode = EditSupplierCode.Trim(),
                            SupplierName = string.IsNullOrWhiteSpace(EditSupplierName)
                                ? EditSupplierCode.Trim()
                                : EditSupplierName.Trim(),
                            IsActive = true
                        })
                    .ConfigureAwait(true);
            }

            StatusMessage = $"Updated thresholds for {EditProductCode}.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenPurchaseOrders() =>
        _navigationService.NavigateTo<PurchaseOrderManagementViewModel>();

    private async Task InitializeAsync()
    {
        try
        {
            var suppliers = await _supplierRepository.GetAllAsync().ConfigureAwait(true);
            foreach (var supplier in suppliers)
            {
                if (!SupplierFilters.Contains(supplier.SupplierCode))
                {
                    SupplierFilters.Add(supplier.SupplierCode);
                }
            }
        }
        catch
        {
            // Supplier table may not exist until bootstrap finishes.
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task LoadProductSettingsAsync(string productCode)
    {
        try
        {
            var item = await _inventoryRepository.GetByProductCodeAsync(productCode).ConfigureAwait(true);
            if (item is null)
            {
                return;
            }

            EditMinReorderQty = item.MinReorderQty > 0 ? item.MinReorderQty : item.StockQuantity;
            EditMaxStockCapacity = item.MaxStockCapacity;
            EditSupplierCode = item.SupplierCode ?? string.Empty;
            EditSupplierName = item.SupplierName ?? string.Empty;
        }
        catch
        {
            // Preview fields remain from the alert row.
        }
    }

    private void RebuildSupplierFilters()
    {
        var selected = SelectedSupplierFilter;
        var codes = _allOpen
            .Select(a => string.IsNullOrWhiteSpace(a.SupplierCode) ? "UNASSIGNED" : a.SupplierCode!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        SupplierFilters.Clear();
        SupplierFilters.Add("All");
        foreach (var code in codes)
        {
            SupplierFilters.Add(code);
        }

        SelectedSupplierFilter = SupplierFilters.Contains(selected) ? selected : "All";
    }

    private void ApplyFilters()
    {
        LowStockItemsList.Clear();
        IEnumerable<InventoryStockAlert> query = _allOpen;

        if (!string.IsNullOrWhiteSpace(SelectedAlertTypeFilter)
            && !SelectedAlertTypeFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(a => a.AlertType.Equals(SelectedAlertTypeFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SelectedSupplierFilter)
            && !SelectedSupplierFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(a =>
                (string.IsNullOrWhiteSpace(a.SupplierCode) ? "UNASSIGNED" : a.SupplierCode!)
                .Equals(SelectedSupplierFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            var q = SearchQuery.Trim();
            query = query.Where(a =>
                a.ProductCode.Contains(q, StringComparison.OrdinalIgnoreCase)
                || a.ProductName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || a.Message.Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var alert in query)
        {
            LowStockItemsList.Add(alert);
        }
    }
}
