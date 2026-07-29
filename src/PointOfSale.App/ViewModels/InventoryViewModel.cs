using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Stock;

namespace PointOfSale.App.ViewModels;

public partial class InventoryViewModel : ObservableObject
{
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly StockManagementService _stockManagementService;
    private readonly ILogger<InventoryViewModel> _logger;

    public InventoryViewModel(
        ILocalInventoryRepository inventoryRepository,
        StockManagementService stockManagementService,
        ILogger<InventoryViewModel> logger)
    {
        _inventoryRepository = inventoryRepository;
        _stockManagementService = stockManagementService;
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
                : "No local inventory items found. Use Sync Warehouse (MRA) or Load demo catalog.";
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
