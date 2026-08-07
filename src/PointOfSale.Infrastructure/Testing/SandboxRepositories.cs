using System.Collections.Concurrent;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.Infrastructure.Testing;

public sealed class SandboxInventoryRepository : ILocalInventoryRepository
{
    private readonly ConcurrentDictionary<string, LocalInventoryItem> _byCode = new(StringComparer.OrdinalIgnoreCase);

    public void Seed(LocalInventoryItem item) => _byCode[item.ProductCode] = item;

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

    public Task<int> DeleteByProductCodesAsync(
        IReadOnlyCollection<string> productCodes,
        CancellationToken cancellationToken = default)
    {
        var removed = 0;
        foreach (var code in productCodes)
        {
            if (!string.IsNullOrWhiteSpace(code) && _byCode.TryRemove(code.Trim(), out _))
            {
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    public Task<int> DeleteByCatalogSourceAsync(
        string catalogSource,
        CancellationToken cancellationToken = default)
    {
        var keys = _byCode
            .Where(kv => string.Equals(kv.Value.CatalogSource, catalogSource, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
        var removed = 0;
        foreach (var key in keys)
        {
            if (_byCode.TryRemove(key, out _))
            {
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    public Task UpdateReorderSettingsAsync(
        string productCode,
        decimal minReorderQty,
        decimal maxStockCapacity,
        string? supplierCode,
        string? supplierName,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ApplyGoodsReceiptAsync(
        string productCode,
        decimal goodQtyReceived,
        decimal unitCost,
        decimal newAverageUnitCost,
        decimal? newRetailPrice,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ApplyHeadOfficeCatalogAsync(
        LocalInventoryItem catalogItem,
        bool preserveLocalStock,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class SandboxConfigurationRepository : IConfigurationRepository
{
    private readonly ConcurrentDictionary<string, string> _json = new(StringComparer.OrdinalIgnoreCase);

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
