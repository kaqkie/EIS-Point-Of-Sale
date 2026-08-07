using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Stock;
using PointOfSale.Mra.Contracts.Utilities;

namespace PointOfSale.App.ViewModels;

public partial class InventoryViewModel : ObservableObject
{
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly StockManagementService _stockManagementService;
    private readonly IPosConfigurationService _posConfigurationService;
    private readonly ILogger<InventoryViewModel> _logger;

    public InventoryViewModel(
        ILocalInventoryRepository inventoryRepository,
        StockManagementService stockManagementService,
        IPosConfigurationService posConfigurationService,
        ILogger<InventoryViewModel> logger)
    {
        _inventoryRepository = inventoryRepository;
        _stockManagementService = stockManagementService;
        _posConfigurationService = posConfigurationService;
        _logger = logger;
        Items = new ObservableCollection<LocalInventoryItem>();
        StatusMessage = "Loading inventory…";
        _ = RefreshAsync();
    }

    public ObservableCollection<LocalInventoryItem> Items { get; }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasItems;

    [ObservableProperty]
    private bool _showEmptyState = true;

    [ObservableProperty]
    private string _newProductCode = string.Empty;

    [ObservableProperty]
    private string _newProductName = string.Empty;

    [ObservableProperty]
    private string _newProductDescription = string.Empty;

    [ObservableProperty]
    private string _newHsCode = string.Empty;

    [ObservableProperty]
    private string _newUnitOfMeasure = "EA";

    [ObservableProperty]
    private string _newTaxRateId = "A";

    [ObservableProperty]
    private string _newUnitPriceText = "0.00";

    [ObservableProperty]
    private string _newOpeningStockText = "0";

    [ObservableProperty]
    private LocalInventoryItem? _selectedItem;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editProductCode = string.Empty;

    [ObservableProperty]
    private string _editProductName = string.Empty;

    [ObservableProperty]
    private string _editHsCode = string.Empty;

    [ObservableProperty]
    private string _editUnitOfMeasure = "EA";

    [ObservableProperty]
    private string _editTaxRateId = "A";

    [ObservableProperty]
    private string _editUnitPriceText = "0.00";

    [ObservableProperty]
    private string _editStockText = "0";

    partial void OnSelectedItemChanged(LocalInventoryItem? value)
    {
        if (value is null)
        {
            IsEditing = false;
            ClearEditForm();
            return;
        }

        IsEditing = true;
        EditProductCode = value.ProductCode;
        EditProductName = value.Name;
        EditHsCode = value.HsCode ?? string.Empty;
        EditUnitOfMeasure = string.IsNullOrWhiteSpace(value.UnitOfMeasure) ? "EA" : value.UnitOfMeasure;
        EditTaxRateId = string.IsNullOrWhiteSpace(value.TaxRateId) ? "A" : value.TaxRateId;
        EditUnitPriceText = value.UnitPrice.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture);
        EditStockText = value.StockQuantity.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var rows = await _inventoryRepository.GetAllAsync().ConfigureAwait(true);
            Items.Clear();
            foreach (var row in rows)
            {
                Items.Add(row);
            }

            HasItems = Items.Count > 0;
            ShowEmptyState = !HasItems;
            StatusMessage = HasItems
                ? $"Loaded {Items.Count} local items."
                : "No local inventory items found. Use Sync EIS Products or Sync Warehouse.";
            _logger.LogInformation("Inventory refresh returned {Count} items.", Items.Count);
        }
        catch (Exception ex)
        {
            HasItems = Items.Count > 0;
            ShowEmptyState = !HasItems;
            StatusMessage = $"Inventory load failed: {ex.Message}";
            _logger.LogError(ex, "Failed to load LocalInventory into the inventory grid.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncSiteProductsAsync()
    {
        IsBusy = true;
        try
        {
            var context = await _posConfigurationService.GetRuntimeContextAsync().ConfigureAwait(true);
            var tin = context.SellerTin?.Trim();
            var siteId = context.SiteId?.Trim();
            if (string.IsNullOrWhiteSpace(tin) || string.IsNullOrWhiteSpace(siteId))
            {
                StatusMessage = "Activate the terminal first — TIN/siteId required to pull EIS products.";
                return;
            }

            var response = await _stockManagementService
                .GetTerminalSiteProductsAsync(
                    new GetTerminalSiteProductsRequest { Tin = tin, SiteId = siteId },
                    reconcileLocalInventory: true,
                    preserveLocalStock: true)
                .ConfigureAwait(true);

            if (!response.Success)
            {
                StatusMessage = response.Remark ?? "EIS site products sync failed.";
                _logger.LogWarning("get-terminal-site-products failed: {Remark}", StatusMessage);
                return;
            }

            await RefreshAsync().ConfigureAwait(true);
            var count = response.Data?.Count ?? 0;
            StatusMessage = count == 0
                ? "EIS returned 0 products for this site. Assign products to the site in the MRA portal."
                : $"EIS site products synchronized ({count} items).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"EIS site products sync failed: {ex.Message}";
            _logger.LogError(ex, "get-terminal-site-products threw.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncWarehouseAsync()
    {
        IsBusy = true;
        try
        {
            var upserted = 0;
            var page = 1;
            const int pageSize = 100;
            var totalPages = 1;

            while (page <= totalPages)
            {
                var response = await _stockManagementService
                    .GetWarehouseInventoryAsync(new WarehouseInventoryRequest { Page = page, PageSize = pageSize })
                    .ConfigureAwait(true);

                if (!response.Success || response.Data is null)
                {
                    StatusMessage = response.Remark ?? "Warehouse sync failed.";
                    _logger.LogWarning("MRA warehouse sync failed on page {Page}: {Remark}", page, StatusMessage);
                    return;
                }

                var total = response.Data.ResolveTotal();
                totalPages = total > 0
                    ? Math.Max(1, (int)Math.Ceiling(total / (double)pageSize))
                    : page;

                foreach (var item in response.Data.GetItems())
                {
                    var productCode = item.ResolveProductCode();
                    if (string.IsNullOrWhiteSpace(productCode))
                    {
                        continue;
                    }

                    var existing = await _inventoryRepository.GetByProductCodeAsync(productCode).ConfigureAwait(true);
                    var unitPrice = item.HasUnitPrice
                        ? item.ResolveUnitPrice()
                        : existing?.UnitPrice ?? 0m;

                    await _inventoryRepository.UpsertAsync(
                        new LocalInventoryItem
                        {
                            ProductId = existing?.ProductId ?? productCode,
                            ProductCode = productCode,
                            Name = item.ResolveName(),
                            UnitPrice = unitPrice,
                            StockQuantity = item.ResolveQuantity(),
                            UnitOfMeasure = item.ResolveUnitOfMeasure() ?? existing?.UnitOfMeasure,
                            HsCode = existing?.HsCode,
                            TaxRateId = existing?.TaxRateId ?? "A",
                            CatalogSource = "MraWarehouse",
                            MinReorderQty = existing?.MinReorderQty ?? 0m,
                            MaxStockCapacity = existing?.MaxStockCapacity ?? 0m,
                            SupplierCode = existing?.SupplierCode,
                            SupplierName = existing?.SupplierName,
                            AverageUnitCost = existing?.AverageUnitCost ?? 0m,
                            MarkupPercent = existing?.MarkupPercent ?? 0m
                        }).ConfigureAwait(true);
                    upserted++;
                }

                if (response.Data.GetItems().Count == 0)
                {
                    break;
                }

                page++;
            }

            await RefreshAsync().ConfigureAwait(true);
            StatusMessage = upserted == 0
                ? "Warehouse sync succeeded but returned no products."
                : $"Warehouse inventory synchronized ({upserted} products).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Warehouse sync failed: {ex.Message}";
            _logger.LogError(ex, "MRA warehouse inventory sync threw.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadDemoCatalogAsync()
    {
        IsBusy = true;
        try
        {
            foreach (var item in DemoCatalogItems)
            {
                await _inventoryRepository.UpsertAsync(item).ConfigureAwait(true);
            }

            await RefreshAsync().ConfigureAwait(true);
            StatusMessage = $"Demo catalog loaded ({DemoCatalogItems.Count} products).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Demo catalog load failed: {ex.Message}";
            _logger.LogError(ex, "Failed to upsert demo LocalInventory catalog.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RemoveDemoCatalogAsync()
    {
        IsBusy = true;
        try
        {
            var bySource = await _inventoryRepository
                .DeleteByCatalogSourceAsync("Demo")
                .ConfigureAwait(true);
            var demoCodes = DemoCatalogItems.Select(i => i.ProductCode).ToArray();
            var byCode = await _inventoryRepository
                .DeleteByProductCodesAsync(demoCodes)
                .ConfigureAwait(true);
            var removed = bySource + byCode;

            await RefreshAsync().ConfigureAwait(true);
            StatusMessage = removed == 0
                ? "No demo catalog products found to remove."
                : $"Removed {removed} demo catalog product(s).";
            _logger.LogInformation("Removed {Count} demo catalog products.", removed);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Remove demo catalog failed: {ex.Message}";
            _logger.LogError(ex, "Failed to remove demo LocalInventory catalog.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task AddLocalProductAsync()
    {
        if (!TryBuildNewProduct(out var item, out var error))
        {
            StatusMessage = error;
            return;
        }

        IsBusy = true;
        try
        {
            var existing = await _inventoryRepository.GetByProductCodeAsync(item.ProductCode).ConfigureAwait(true);
            if (existing is not null)
            {
                StatusMessage = $"Product code '{item.ProductCode}' already exists.";
                return;
            }

            await _inventoryRepository.UpsertAsync(item).ConfigureAwait(true);
            ClearNewProductForm();
            await RefreshAsync().ConfigureAwait(true);
            StatusMessage =
                $"Added '{item.Name}' to the local catalog only (not MRA warehouse). " +
                "Use Register with MRA to publish it, then Sync Warehouse.";
            _logger.LogInformation("Admin added local product {ProductCode}.", item.ProductCode);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Add product failed: {ex.Message}";
            _logger.LogError(ex, "Failed to add local inventory product.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RegisterProductWithMraAsync()
    {
        if (!TryBuildAddProductRequest(out var request, out var error))
        {
            StatusMessage = error;
            return;
        }

        IsBusy = true;
        try
        {
            var context = await _posConfigurationService.GetRuntimeContextAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(context.SiteId))
            {
                request = new AddProductRequest
                {
                    Barcode = request.Barcode,
                    HsCode = request.HsCode,
                    Name = request.Name,
                    Description = request.Description,
                    Uom = request.Uom,
                    UnitPrice = request.UnitPrice,
                    OpeningStockQuantity = request.OpeningStockQuantity,
                    ExpectedTaxRateId = request.ExpectedTaxRateId,
                    SiteId = context.SiteId
                };
            }

            var result = await _stockManagementService.AddProductAsync(request).ConfigureAwait(true);
            if (!result.Success)
            {
                StatusMessage = result.Remark ?? "MRA add-product failed.";
                return;
            }

            ClearNewProductForm();
            await RefreshAsync().ConfigureAwait(true);
            StatusMessage = string.IsNullOrWhiteSpace(result.Remark)
                ? $"Registered '{request.Name}' with MRA and cached locally (price {request.UnitPrice:N2} incl. VAT)."
                : result.Remark;
        }
        catch (Exception ex)
        {
            StatusMessage = $"MRA register failed: {ex.Message}";
            _logger.LogError(ex, "stock/add-product failed from admin inventory.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        SelectedItem = null;
    }

    [RelayCommand]
    private async Task SaveEditedProductAsync()
    {
        var original = SelectedItem;
        if (original is null)
        {
            StatusMessage = "Select a product in the grid to edit.";
            return;
        }

        var name = (EditProductName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Enter a product name.";
            return;
        }

        if (!decimal.TryParse(EditUnitPriceText, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.CurrentCulture, out var price)
            && !decimal.TryParse(EditUnitPriceText, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out price))
        {
            StatusMessage = "Enter a valid VAT-inclusive unit price.";
            return;
        }

        if (price < 0m)
        {
            StatusMessage = "Unit price cannot be negative.";
            return;
        }

        if (!decimal.TryParse(EditStockText, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.CurrentCulture, out var stock)
            && !decimal.TryParse(EditStockText, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out stock))
        {
            StatusMessage = "Enter a valid stock quantity.";
            return;
        }

        if (stock < 0m)
        {
            StatusMessage = "Stock cannot be negative.";
            return;
        }

        IsBusy = true;
        try
        {
            var updated = new LocalInventoryItem
            {
                ProductId = original.ProductId,
                ProductCode = original.ProductCode,
                Name = name,
                UnitPrice = PosTaxCalculator.RoundMoney(price),
                StockQuantity = PosTaxCalculator.RoundMoney(stock),
                HsCode = string.IsNullOrWhiteSpace(EditHsCode) ? null : EditHsCode.Trim(),
                UnitOfMeasure = string.IsNullOrWhiteSpace(EditUnitOfMeasure) ? "EA" : EditUnitOfMeasure.Trim(),
                TaxRateId = string.IsNullOrWhiteSpace(EditTaxRateId) ? "A" : EditTaxRateId.Trim(),
                CatalogSource = original.CatalogSource,
                HeadOfficeRevisionUtc = original.HeadOfficeRevisionUtc,
                LastReplicatedAtUtc = original.LastReplicatedAtUtc,
                MinReorderQty = original.MinReorderQty,
                MaxStockCapacity = original.MaxStockCapacity,
                SupplierCode = original.SupplierCode,
                SupplierName = original.SupplierName,
                AverageUnitCost = original.AverageUnitCost,
                MarkupPercent = original.MarkupPercent
            };

            await _inventoryRepository.UpsertAsync(updated).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            SelectedItem = Items.FirstOrDefault(i =>
                string.Equals(i.ProductCode, updated.ProductCode, StringComparison.OrdinalIgnoreCase));
            StatusMessage =
                $"Updated '{updated.Name}' locally (price {updated.UnitPrice:N2} incl. VAT, stock {updated.StockQuantity:0.##}). " +
                "MRA master/warehouse fields may still need a portal change.";
            _logger.LogInformation("Admin updated local product {ProductCode}.", updated.ProductCode);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save product failed: {ex.Message}";
            _logger.LogError(ex, "Failed to save edited inventory product.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool TryBuildNewProduct(out LocalInventoryItem item, out string error)
    {
        item = null!;
        error = string.Empty;

        var code = (NewProductCode ?? string.Empty).Trim();
        var name = (NewProductName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            error = "Enter a product code / barcode.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Enter a product name.";
            return false;
        }

        if (!decimal.TryParse(NewUnitPriceText, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.CurrentCulture, out var price)
            && !decimal.TryParse(NewUnitPriceText, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out price))
        {
            error = "Enter a valid VAT-inclusive unit price.";
            return false;
        }

        if (price < 0m)
        {
            error = "Unit price cannot be negative.";
            return false;
        }

        if (!decimal.TryParse(NewOpeningStockText, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.CurrentCulture, out var stock)
            && !decimal.TryParse(NewOpeningStockText, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out stock))
        {
            stock = 0m;
        }

        if (stock < 0m)
        {
            error = "Opening stock cannot be negative.";
            return false;
        }

        var uom = string.IsNullOrWhiteSpace(NewUnitOfMeasure) ? "EA" : NewUnitOfMeasure.Trim();
        var tax = string.IsNullOrWhiteSpace(NewTaxRateId) ? "A" : NewTaxRateId.Trim();
        var hs = string.IsNullOrWhiteSpace(NewHsCode) ? null : NewHsCode.Trim();

        item = new LocalInventoryItem
        {
            ProductId = code,
            ProductCode = code,
            Name = name,
            UnitPrice = PosTaxCalculator.RoundMoney(price),
            StockQuantity = PosTaxCalculator.RoundMoney(stock),
            HsCode = hs,
            UnitOfMeasure = uom,
            TaxRateId = tax,
            CatalogSource = "Local"
        };
        return true;
    }

    private bool TryBuildAddProductRequest(out AddProductRequest request, out string error)
    {
        request = null!;
        if (!TryBuildNewProduct(out var item, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.HsCode))
        {
            error = "HS code is required to register the product with MRA.";
            return false;
        }

        var description = string.IsNullOrWhiteSpace(NewProductDescription)
            ? item.Name
            : NewProductDescription.Trim();

        request = new AddProductRequest
        {
            Barcode = item.ProductCode,
            HsCode = item.HsCode!,
            Name = item.Name,
            Description = description,
            Uom = item.UnitOfMeasure ?? "EA",
            UnitPrice = item.UnitPrice,
            OpeningStockQuantity = item.StockQuantity,
            ExpectedTaxRateId = item.TaxRateId
        };
        return true;
    }

    private void ClearNewProductForm()
    {
        NewProductCode = string.Empty;
        NewProductName = string.Empty;
        NewProductDescription = string.Empty;
        NewHsCode = string.Empty;
        NewUnitOfMeasure = "EA";
        NewTaxRateId = "A";
        NewUnitPriceText = "0.00";
        NewOpeningStockText = "0";
    }

    private void ClearEditForm()
    {
        EditProductCode = string.Empty;
        EditProductName = string.Empty;
        EditHsCode = string.Empty;
        EditUnitOfMeasure = "EA";
        EditTaxRateId = "A";
        EditUnitPriceText = "0.00";
        EditStockText = "0";
    }

    private static IReadOnlyList<LocalInventoryItem> DemoCatalogItems { get; } =
    [
        CreateDemo("ART-WATER-500", "Bottled Water 500ml", 350m, 120m, "2201", 24m, 240m, 250m),
        CreateDemo("ART-BREAD-WHT", "White Bread Loaf", 1500m, 40m, "1905", 12m, 80m, 1100m),
        CreateDemo("ART-SOAP-250", "Bar Soap 250g", 800m, 75m, "3401", 20m, 150m, 580m),
        CreateDemo("ART-RICE-2KG", "Rice 2kg", 4500m, 60m, "1006", 15m, 120m, 3400m),
        CreateDemo("ART-OIL-1L", "Cooking Oil 1L", 5200m, 35m, "1507", 10m, 80m, 4000m),
        CreateDemo("ART-SUGAR-1KG", "Sugar 1kg", 2800m, 50m, "1701", 12m, 100m, 2100m),
        CreateDemo("ART-MILK-1L", "Fresh Milk 1L", 2200m, 28m, "0401", 10m, 60m, 1650m),
        CreateDemo("ART-EGGS-12", "Eggs (dozen)", 3500m, 22m, "0407", 8m, 48m, 2600m)
    ];

    private static LocalInventoryItem CreateDemo(
        string code,
        string name,
        decimal price,
        decimal stock,
        string hsCode,
        decimal minReorder,
        decimal maxCapacity,
        decimal averageCost) =>
        new()
        {
            ProductId = code,
            ProductCode = code,
            Name = name,
            UnitPrice = price,
            StockQuantity = stock,
            HsCode = hsCode,
            UnitOfMeasure = "EA",
            TaxRateId = "A",
            CatalogSource = "Demo",
            MinReorderQty = minReorder,
            MaxStockCapacity = maxCapacity,
            SupplierCode = "SUP-LOCAL",
            SupplierName = "Local Supplier",
            AverageUnitCost = averageCost,
            MarkupPercent = 25m
        };
}
