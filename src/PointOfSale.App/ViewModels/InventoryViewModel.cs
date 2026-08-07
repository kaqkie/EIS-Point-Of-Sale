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
            var response = await _stockManagementService
                .GetWarehouseInventoryAsync(new WarehouseInventoryRequest { Page = 1, PageSize = 50 })
                .ConfigureAwait(true);

            if (!response.Success || response.Data is null)
            {
                StatusMessage = response.Remark ?? "Warehouse sync failed.";
                _logger.LogWarning("MRA warehouse sync failed: {Remark}", StatusMessage);
                return;
            }

            var upserted = 0;
            foreach (var item in response.Data.GetItems())
            {
                var productCode = item.ResolveProductCode();
                if (string.IsNullOrWhiteSpace(productCode))
                {
                    continue;
                }

                await _inventoryRepository.UpsertAsync(
                    new LocalInventoryItem
                    {
                        ProductId = productCode,
                        ProductCode = productCode,
                        Name = item.ResolveName(),
                        UnitPrice = item.ResolveUnitPrice(),
                        StockQuantity = item.ResolveQuantity(),
                        UnitOfMeasure = item.ResolveUnitOfMeasure(),
                        CatalogSource = "MraWarehouse"
                    }).ConfigureAwait(true);
                upserted++;
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
                $"Added '{item.Name}' locally at {item.UnitPrice:N2} (VAT-inclusive shelf price).";
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
            var result = await _stockManagementService.AddProductAsync(request).ConfigureAwait(true);
            if (!result.Success)
            {
                StatusMessage = result.Remark ?? "MRA add-product failed.";
                return;
            }

            ClearNewProductForm();
            await RefreshAsync().ConfigureAwait(true);
            StatusMessage =
                $"Registered '{request.Name}' with MRA and cached locally (price {request.UnitPrice:N2} incl. VAT).";
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
            CatalogSource = "Local",
            MinReorderQty = minReorder,
            MaxStockCapacity = maxCapacity,
            SupplierCode = "SUP-LOCAL",
            SupplierName = "Local Supplier",
            AverageUnitCost = averageCost,
            MarkupPercent = 25m
        };
}
