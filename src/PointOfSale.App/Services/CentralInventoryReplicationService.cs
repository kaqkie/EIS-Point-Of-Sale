using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface ICentralInventoryReplicationService
{
    Task<CatalogReplicationResult> PullAndApplyCatalogAsync(
        HttpClient httpClient,
        Uri catalogDeltaUri,
        DateTime? sinceUtc,
        bool activeSalesShiftOpen,
        CancellationToken cancellationToken = default);
}

public sealed class CatalogReplicationResult
{
    public int ProductsReceived { get; init; }
    public int ProductsApplied { get; init; }
    public int LocalStockPreserved { get; init; }
    public DateTime? CatalogRevisionUtc { get; init; }
    public string? Error { get; init; }
    public bool Success => Error is null;
}

public sealed class CentralInventoryReplicationService : ICentralInventoryReplicationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly ILogger<CentralInventoryReplicationService> _logger;

    public CentralInventoryReplicationService(
        ILocalInventoryRepository inventoryRepository,
        ILogger<CentralInventoryReplicationService> logger)
    {
        _inventoryRepository = inventoryRepository;
        _logger = logger;
    }

    public async Task<CatalogReplicationResult> PullAndApplyCatalogAsync(
        HttpClient httpClient,
        Uri catalogDeltaUri,
        DateTime? sinceUtc,
        bool activeSalesShiftOpen,
        CancellationToken cancellationToken = default)
    {
        var uri = sinceUtc is null
            ? catalogDeltaUri
            : new UriBuilder(catalogDeltaUri)
            {
                Query = $"since={Uri.EscapeDataString(sinceUtc.Value.ToString("O"))}"
            }.Uri;

        using var response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new CatalogReplicationResult
            {
                Error = $"Catalog pull failed HTTP {(int)response.StatusCode}: {Truncate(body, 300)}"
            };
        }

        var delta = await response.Content.ReadFromJsonAsync<CentralCatalogDeltaResponse>(JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        if (delta is null)
        {
            return new CatalogReplicationResult { Error = "Catalog delta response was empty." };
        }

        var applied = 0;
        var preserved = 0;
        foreach (var product in delta.Products.Where(p => p.IsActive))
        {
            var preserveStock = CatalogConflictResolver.ShouldPreserveLocalStock(
                activeSalesShiftOpen,
                product.OverrideLocalStock);

            if (preserveStock)
            {
                preserved++;
            }

            var item = new LocalInventoryItem
            {
                ProductId = product.ProductId,
                ProductCode = product.ProductCode,
                Name = product.Name,
                UnitPrice = product.UnitPrice,
                StockQuantity = product.StockQuantity ?? 0m,
                HsCode = product.HsCode,
                UnitOfMeasure = product.UnitOfMeasure,
                TaxRateId = product.TaxRateId,
                CatalogSource = "HeadOffice",
                HeadOfficeRevisionUtc = product.RevisionUtc == default ? delta.CatalogRevisionUtc : product.RevisionUtc
            };

            await _inventoryRepository.ApplyHeadOfficeCatalogAsync(item, preserveStock, cancellationToken)
                .ConfigureAwait(false);
            applied++;
        }

        _logger.LogInformation(
            "Head-office catalog applied: {Applied} products ({Preserved} preserved local stock; shiftOpen={ShiftOpen}).",
            applied,
            preserved,
            activeSalesShiftOpen);

        return new CatalogReplicationResult
        {
            ProductsReceived = delta.Products.Count,
            ProductsApplied = applied,
            LocalStockPreserved = preserved,
            CatalogRevisionUtc = delta.CatalogRevisionUtc
        };
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}

// Explicit static helper for tests without interface statics friction
public static class CatalogConflictResolver
{
    public static bool ShouldPreserveLocalStock(bool activeSalesShiftOpen, bool overrideLocalStockFromHeadOffice) =>
        activeSalesShiftOpen || !overrideLocalStockFromHeadOffice;
}
