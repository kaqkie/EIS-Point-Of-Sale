using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Stock;

namespace PointOfSale.App.ViewModels;

public partial class InventoryViewModel : ObservableObject
{
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly StockManagementService _stockManagementService;

    public InventoryViewModel(
        ILocalInventoryRepository inventoryRepository,
        StockManagementService stockManagementService)
    {
        _inventoryRepository = inventoryRepository;
        _stockManagementService = stockManagementService;
        Items = new ObservableCollection<LocalInventoryItem>();
        _ = RefreshAsync();
    }

    public ObservableCollection<LocalInventoryItem> Items { get; }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Items.Clear();
            var rows = await _inventoryRepository.GetAllAsync().ConfigureAwait(true);
            foreach (var row in rows)
            {
                Items.Add(row);
            }

            StatusMessage = $"Loaded {Items.Count} local items.";
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
                .GetWarehouseInventoryAsync(new WarehouseInventoryRequest { PageNumber = 1, PageSize = 50 })
                .ConfigureAwait(true);

            if (!response.Success || response.Data is null)
            {
                StatusMessage = response.Remark ?? "Warehouse sync failed.";
                return;
            }

            foreach (var item in response.Data.GetItems())
            {
                if (string.IsNullOrWhiteSpace(item.ProductCode))
                {
                    continue;
                }

                await _inventoryRepository.UpsertAsync(
                    new LocalInventoryItem
                    {
                        ProductId = item.ProductId ?? item.ProductCode,
                        ProductCode = item.ProductCode,
                        Name = item.ResolveName(),
                        UnitPrice = item.UnitPrice,
                        StockQuantity = item.ResolveQuantity(),
                        HsCode = item.HsCode,
                        UnitOfMeasure = item.UnitOfMeasure,
                        TaxRateId = item.TaxRateId
                    }).ConfigureAwait(true);
            }

            await RefreshAsync().ConfigureAwait(true);
            StatusMessage = "Warehouse inventory synchronized.";
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
