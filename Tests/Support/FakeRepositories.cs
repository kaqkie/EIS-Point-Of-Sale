using System.Collections.Concurrent;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.Tests.Support;

public sealed class FakeLocalInventoryRepository : ILocalInventoryRepository
{
    private readonly ConcurrentDictionary<string, LocalInventoryItem> _byCode = new(StringComparer.OrdinalIgnoreCase);

    public void Seed(LocalInventoryItem item) =>
        _byCode[item.ProductCode] = item;

    public Task<IReadOnlyList<LocalInventoryItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LocalInventoryItem>>(_byCode.Values.ToList());

    public Task<LocalInventoryItem?> GetByProductCodeAsync(string productCode, CancellationToken cancellationToken = default)
    {
        _byCode.TryGetValue(productCode, out var item);
        return Task.FromResult(item);
    }

    public Task<LocalInventoryItem?> GetByProductIdAsync(string productId, CancellationToken cancellationToken = default)
    {
        var item = _byCode.Values.FirstOrDefault(x =>
            string.Equals(x.ProductId, productId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(item);
    }

    public Task UpsertAsync(LocalInventoryItem item, CancellationToken cancellationToken = default)
    {
        _byCode[item.ProductCode] = item;
        return Task.CompletedTask;
    }

    public Task UpdateReorderSettingsAsync(
        string productCode,
        decimal minReorderQty,
        decimal maxStockCapacity,
        string? supplierCode,
        string? supplierName,
        CancellationToken cancellationToken = default)
    {
        if (_byCode.TryGetValue(productCode, out var item))
        {
            item.MinReorderQty = minReorderQty;
            item.MaxStockCapacity = maxStockCapacity;
            item.SupplierCode = supplierCode;
            item.SupplierName = supplierName;
            _byCode[productCode] = item;
        }

        return Task.CompletedTask;
    }

    public Task ApplyGoodsReceiptAsync(
        string productCode,
        decimal goodQtyReceived,
        decimal unitCost,
        decimal newAverageUnitCost,
        decimal? newRetailPrice,
        CancellationToken cancellationToken = default)
    {
        if (_byCode.TryGetValue(productCode, out var item))
        {
            item.StockQuantity += goodQtyReceived;
            item.AverageUnitCost = newAverageUnitCost;
            if (newRetailPrice is decimal retail)
            {
                item.UnitPrice = retail;
            }

            _byCode[productCode] = item;
        }

        return Task.CompletedTask;
    }

    public Task ApplyHeadOfficeCatalogAsync(
        LocalInventoryItem catalogItem,
        bool preserveLocalStock,
        CancellationToken cancellationToken = default)
    {
        if (_byCode.TryGetValue(catalogItem.ProductCode, out var existing) ||
            (existing = _byCode.Values.FirstOrDefault(x =>
                string.Equals(x.ProductId, catalogItem.ProductId, StringComparison.OrdinalIgnoreCase))) is not null)
        {
            existing.ProductCode = catalogItem.ProductCode;
            existing.Name = catalogItem.Name;
            existing.UnitPrice = catalogItem.UnitPrice;
            if (!preserveLocalStock)
            {
                existing.StockQuantity = catalogItem.StockQuantity;
            }

            existing.HsCode = catalogItem.HsCode;
            existing.UnitOfMeasure = catalogItem.UnitOfMeasure;
            existing.TaxRateId = catalogItem.TaxRateId;
            existing.CatalogSource = "HeadOffice";
            existing.HeadOfficeRevisionUtc = catalogItem.HeadOfficeRevisionUtc;
            existing.LastReplicatedAtUtc = DateTime.UtcNow;
            _byCode[existing.ProductCode] = existing;
        }
        else
        {
            catalogItem.CatalogSource = "HeadOffice";
            catalogItem.LastReplicatedAtUtc = DateTime.UtcNow;
            _byCode[catalogItem.ProductCode] = catalogItem;
        }

        return Task.CompletedTask;
    }
}

public sealed class FakeConfigurationRepository : IConfigurationRepository
{
    private readonly ConcurrentDictionary<string, string> _json = new(StringComparer.OrdinalIgnoreCase);

    public void SetJson(string key, string json) => _json[key] = json;

    public Task<string?> GetJsonAsync(string key, CancellationToken cancellationToken = default)
    {
        _json.TryGetValue(key, out var value);
        return Task.FromResult<string?>(value);
    }

    public Task UpsertJsonAsync(string key, string json, CancellationToken cancellationToken = default)
    {
        _json[key] = json;
        return Task.CompletedTask;
    }

    public Task<string?> GetProtectedSecretPlainAsync(string key, CancellationToken cancellationToken = default) =>
        GetJsonAsync(key, cancellationToken);

    public Task UpsertProtectedSecretAsync(string key, string plainSecret, CancellationToken cancellationToken = default) =>
        UpsertJsonAsync(key, plainSecret, cancellationToken);
}
