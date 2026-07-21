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

    public Task UpsertAsync(LocalInventoryItem item, CancellationToken cancellationToken = default)
    {
        _byCode[item.ProductCode] = item;
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
