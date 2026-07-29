using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Serialization;
using PointOfSale.Mra.Services;

namespace PointOfSale.Infrastructure.Services;

public interface ITerminalSiteProductsCatalogSyncService
{
    /// <summary>
    /// Parses an EIS <c>get-terminal-site-products</c> JSON body and upserts usable rows into local inventory.
    /// </summary>
    Task<TerminalSiteProductsSyncResult> SyncFromJsonAsync(
        string? json,
        string tin,
        string siteId,
        bool preserveLocalStock = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists already-parsed catalog snapshots into <c>dbo.LocalInventory</c> for POS lookup.
    /// </summary>
        Task<TerminalSiteProductsSyncResult> SyncFromSnapshotsAsync(
        IReadOnlyList<TerminalSiteProductCatalogSnapshot> snapshots,
        string? tin = null,
        string? siteId = null,
        bool preserveLocalStock = true,
        string? remark = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Ingests MRA terminal-site product/service catalog into Albert Retail Terminal local inventory.
/// </summary>
public sealed class TerminalSiteProductsCatalogSyncService : ITerminalSiteProductsCatalogSyncService
{
    public const string CatalogSource = "MraEis";

    private readonly ITerminalSiteProductsResponseService _parser;
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ILogger<TerminalSiteProductsCatalogSyncService> _logger;

    public TerminalSiteProductsCatalogSyncService(
        ITerminalSiteProductsResponseService parser,
        ILocalInventoryRepository inventoryRepository,
        IConfigurationRepository configurationRepository,
        ILogger<TerminalSiteProductsCatalogSyncService> logger)
    {
        _parser = parser;
        _inventoryRepository = inventoryRepository;
        _configurationRepository = configurationRepository;
        _logger = logger;
    }

    public async Task<TerminalSiteProductsSyncResult> SyncFromJsonAsync(
        string? json,
        string tin,
        string siteId,
        bool preserveLocalStock = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tin);
        ArgumentException.ThrowIfNullOrWhiteSpace(siteId);

        var parsed = _parser.ParseJson(json);
        if (!parsed.Success)
        {
            return TerminalSiteProductsSyncResult.Failed(
                parsed.Remark ?? "Unable to parse get-terminal-site-products response.",
                parsed.ErrorDetail,
                parsed.StatusCode,
                parsed.Errors);
        }

        return await SyncFromSnapshotsAsync(
                parsed.Snapshots,
                tin,
                siteId,
                preserveLocalStock,
                parsed.Remark,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TerminalSiteProductsSyncResult> SyncFromSnapshotsAsync(
        IReadOnlyList<TerminalSiteProductCatalogSnapshot> snapshots,
        string? tin = null,
        string? siteId = null,
        bool preserveLocalStock = true,
        string? remark = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var cacheKey = !string.IsNullOrWhiteSpace(tin) && !string.IsNullOrWhiteSpace(siteId)
            ? StockManagementService.BuildTerminalSiteProductsCacheKey(tin, siteId)
            : null;
        if (cacheKey is not null)
        {
            await _configurationRepository.UpsertJsonAsync(
                    cacheKey,
                    JsonSerializer.Serialize(snapshots, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var syncedAt = DateTime.UtcNow;
        var upserted = 0;
        var products = 0;
        var services = 0;

        foreach (var snapshot in snapshots)
        {
            var item = new LocalInventoryItem
            {
                ProductId = snapshot.ProductId,
                ProductCode = snapshot.ProductCode,
                Name = snapshot.Name,
                UnitPrice = snapshot.UnitPrice,
                StockQuantity = snapshot.StockQuantity,
                UnitOfMeasure = snapshot.UnitOfMeasure,
                TaxRateId = snapshot.TaxRateId,
                HsCode = snapshot.HsCode,
                CatalogSource = CatalogSource,
                HeadOfficeRevisionUtc = syncedAt,
                LastReplicatedAtUtc = syncedAt,
                MinReorderQty = snapshot.MinimumStockLevel
            };

            if (preserveLocalStock)
            {
                var existing = await _inventoryRepository
                    .GetByProductCodeAsync(snapshot.ProductCode, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is not null)
                {
                    item.StockQuantity = existing.StockQuantity;
                    item.AverageUnitCost = existing.AverageUnitCost;
                    item.MarkupPercent = existing.MarkupPercent;
                    item.SupplierCode = existing.SupplierCode;
                    item.SupplierName = existing.SupplierName;
                    if (existing.MaxStockCapacity > 0)
                    {
                        item.MaxStockCapacity = existing.MaxStockCapacity;
                    }
                }
            }

            await _inventoryRepository.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
            upserted++;
            if (snapshot.IsProduct)
            {
                products++;
            }
            else
            {
                services++;
            }
        }

        _logger.LogInformation(
            "Synced MRA site catalog tin={Tin} siteId={SiteId} upserted={Upserted} products={Products} services={Services} preserveStock={Preserve}",
            tin?.Trim() ?? "(none)",
            siteId?.Trim() ?? "(none)",
            upserted,
            products,
            services,
            preserveLocalStock);

        return TerminalSiteProductsSyncResult.Succeeded(
            upserted,
            products,
            services,
            remark,
            snapshots);
    }
}

public sealed class TerminalSiteProductsSyncResult
{
    public bool Success { get; init; }
    public string? Remark { get; init; }
    public string? ErrorDetail { get; init; }
    public int StatusCode { get; init; }
    public IReadOnlyList<EisApiError>? Errors { get; init; }
    public int UpsertedCount { get; init; }
    public int ProductCount { get; init; }
    public int ServiceCount { get; init; }
    public IReadOnlyList<TerminalSiteProductCatalogSnapshot> Snapshots { get; init; } =
        Array.Empty<TerminalSiteProductCatalogSnapshot>();

    public static TerminalSiteProductsSyncResult Succeeded(
        int upserted,
        int products,
        int services,
        string? remark,
        IReadOnlyList<TerminalSiteProductCatalogSnapshot> snapshots) =>
        new()
        {
            Success = true,
            UpsertedCount = upserted,
            ProductCount = products,
            ServiceCount = services,
            Remark = remark,
            StatusCode = 1,
            Snapshots = snapshots
        };

    public static TerminalSiteProductsSyncResult Failed(
        string remark,
        string? errorDetail = null,
        int statusCode = 0,
        IReadOnlyList<EisApiError>? errors = null) =>
        new()
        {
            Success = false,
            Remark = remark,
            ErrorDetail = errorDetail,
            StatusCode = statusCode,
            Errors = errors
        };
}
