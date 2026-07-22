using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.ViewModels;

public partial class PurchaseOrderManagementViewModel : ObservableObject
{
    private readonly IPurchaseOrderGenerationService _poService;
    private readonly IInventorySupplierRepository _supplierRepository;
    private readonly IAuthenticationAuthorizationService _auth;

    public PurchaseOrderManagementViewModel(
        IPurchaseOrderGenerationService poService,
        IInventorySupplierRepository supplierRepository,
        IAuthenticationAuthorizationService auth)
    {
        _poService = poService;
        _supplierRepository = supplierRepository;
        _auth = auth;
        Orders = new ObservableCollection<PurchaseOrder>();
        OrderLines = new ObservableCollection<PurchaseOrderLine>();
        SupplierFilters = new ObservableCollection<string> { "All" };
        SelectedSupplierFilter = "All";
        _ = InitializeAsync();
    }

    public ObservableCollection<PurchaseOrder> Orders { get; }
    public ObservableCollection<PurchaseOrderLine> OrderLines { get; }
    public ObservableCollection<string> SupplierFilters { get; }

    [ObservableProperty]
    private string _selectedSupplierFilter = "All";

    [ObservableProperty]
    private PurchaseOrder? _selectedOrder;

    [ObservableProperty]
    private string _generatedPoSummary = "Generate POs from current low-stock alerts.";

    [ObservableProperty]
    private bool _isGeneratingPo;

    [ObservableProperty]
    private string _statusMessage = "Review supplier purchase orders before export.";

    [ObservableProperty]
    private string _newSupplierCode = string.Empty;

    [ObservableProperty]
    private string _newSupplierName = string.Empty;

    partial void OnSelectedOrderChanged(PurchaseOrder? value) => _ = LoadLinesAsync(value);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManagePurchaseOrders);
            await LoadOrdersAsync().ConfigureAwait(true);
            StatusMessage = $"Loaded {Orders.Count} purchase order(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task GeneratePoAsync()
    {
        if (IsGeneratingPo)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManagePurchaseOrders);
            IsGeneratingPo = true;
            var result = await _poService.GenerateFromLowStockAsync(
                    SelectedSupplierFilter,
                    _auth.CurrentOperator?.Username)
                .ConfigureAwait(true);

            GeneratedPoSummary = result.GeneratedPoSummary;
            StatusMessage = result.Message;
            await LoadOrdersAsync().ConfigureAwait(true);
            if (result.Orders.Count > 0)
            {
                SelectedOrder = Orders.FirstOrDefault(o => o.PoId == result.Orders[0].PoId);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            GeneratedPoSummary = ex.Message;
        }
        finally
        {
            IsGeneratingPo = false;
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManagePurchaseOrders);
            if (SelectedOrder is null)
            {
                StatusMessage = "Select a purchase order to export.";
                return;
            }

            var path = await _poService.ExportCsvAsync(SelectedOrder.PoId).ConfigureAwait(true);
            StatusMessage = string.IsNullOrEmpty(path)
                ? "CSV export cancelled."
                : $"CSV exported to {path}";
            await LoadOrdersAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManagePurchaseOrders);
            if (SelectedOrder is null)
            {
                StatusMessage = "Select a purchase order to export.";
                return;
            }

            await _poService.ExportPdfAsync(SelectedOrder.PoId).ConfigureAwait(true);
            StatusMessage = $"PDF/print export completed for {SelectedOrder.PoNumber}.";
            await LoadOrdersAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveSupplierAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManagePurchaseOrders);
            if (string.IsNullOrWhiteSpace(NewSupplierCode) || string.IsNullOrWhiteSpace(NewSupplierName))
            {
                StatusMessage = "Supplier code and name are required.";
                return;
            }

            await _supplierRepository.UpsertAsync(
                    new InventorySupplier
                    {
                        SupplierCode = NewSupplierCode.Trim().ToUpperInvariant(),
                        SupplierName = NewSupplierName.Trim(),
                        IsActive = true
                    })
                .ConfigureAwait(true);

            if (!SupplierFilters.Contains(NewSupplierCode.Trim().ToUpperInvariant()))
            {
                SupplierFilters.Add(NewSupplierCode.Trim().ToUpperInvariant());
            }

            StatusMessage = $"Saved supplier {NewSupplierCode}.";
            NewSupplierCode = string.Empty;
            NewSupplierName = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            var suppliers = await _supplierRepository.GetAllAsync(activeOnly: false).ConfigureAwait(true);
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
            // Bootstrap may still be applying schema.
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    private async Task LoadOrdersAsync()
    {
        Orders.Clear();
        var rows = await _poService.GetRecentOrdersAsync().ConfigureAwait(true);
        foreach (var row in rows)
        {
            Orders.Add(row);
        }
    }

    private async Task LoadLinesAsync(PurchaseOrder? order)
    {
        OrderLines.Clear();
        if (order is null)
        {
            return;
        }

        try
        {
            var lines = await _poService.GetLinesAsync(order.PoId).ConfigureAwait(true);
            foreach (var line in lines)
            {
                OrderLines.Add(line);
            }

            GeneratedPoSummary = order.SummaryText ?? GeneratedPoSummary;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}
