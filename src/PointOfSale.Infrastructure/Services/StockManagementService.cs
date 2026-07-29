using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Inventory;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Contracts.Stock;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Serialization;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.Infrastructure.Services;

public sealed class StockManagementService
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    private readonly MraApiClient _apiClient;
    private readonly IMraTerminalAuthProvider _authProvider;
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ILogger<StockManagementService> _logger;
    private readonly int _inventoryUploadBatchSize;

    public StockManagementService(
        MraApiClient apiClient,
        IMraTerminalAuthProvider authProvider,
        ILocalInventoryRepository inventoryRepository,
        IConfigurationRepository configurationRepository,
        ILogger<StockManagementService> logger,
        IOptions<PosOperationsOptions> posOperations)
    {
        _apiClient = apiClient;
        _authProvider = authProvider;
        _inventoryRepository = inventoryRepository;
        _configurationRepository = configurationRepository;
        _logger = logger;
        _inventoryUploadBatchSize = Math.Clamp(posOperations.Value.InventoryUploadBatchSize, 1, MaxPageSize);
    }

    public async Task<StockResult<PagedResponse<WarehouseInventoryItemDto>>> GetWarehouseInventoryAsync(
        WarehouseInventoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var pageSize = NormalizeInventoryPageSize(request.PageSize);
        var context = await _authProvider.GetJwtContextAsync(cancellationToken).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["pageNumber"] = Math.Max(1, request.PageNumber).ToString(),
            ["pageSize"] = pageSize.ToString()
        };

        if (!string.IsNullOrWhiteSpace(request.SiteId))
        {
            query["siteId"] = request.SiteId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.WarehouseId))
        {
            query["warehouseId"] = request.WarehouseId.Trim();
        }

        var response = await _apiClient
            .GetAsync<PagedResponse<WarehouseInventoryItemDto>>("stock/warehouse-inventory", query, context, cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<PagedResponse<RawMaterialItemDto>>> GetRawMaterialAsync(
        RawMaterialRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = await _authProvider.GetJwtContextAsync(cancellationToken).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["pageNumber"] = Math.Max(1, request.PageNumber).ToString(),
            ["pageSize"] = NormalizeInventoryPageSize(request.PageSize).ToString()
        };

        var response = await _apiClient
            .GetAsync<PagedResponse<RawMaterialItemDto>>("raw-material/get-raw-material", query, context, cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<IReadOnlyList<HsCodeDto>>> GetHsCodesAsync(CancellationToken cancellationToken = default)
    {
        var context = await _authProvider.GetJwtContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .GetAsync<IReadOnlyList<HsCodeDto>>("stock/get-hs-codes", context, cancellationToken)
            .ConfigureAwait(false);

        var result = ToResult(response);
        if (result.Success && result.Data is not null)
        {
            await CacheReferenceDataAsync(MraConfigurationKeys.StockHsCodesCache, result.Data, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    public async Task<StockResult<IReadOnlyList<UnitOfMeasureDto>>> GetUnitsOfMeasureAsync(
        CancellationToken cancellationToken = default)
    {
        var context = await _authProvider.GetJwtContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .GetAsync<IReadOnlyList<UnitOfMeasureDto>>("stock/get-units-of-measure", context, cancellationToken)
            .ConfigureAwait(false);

        var result = ToResult(response);
        if (result.Success && result.Data is not null)
        {
            await CacheReferenceDataAsync(MraConfigurationKeys.StockUnitsOfMeasureCache, result.Data, cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// <c>POST /api/v1/utilities/get-terminal-site-products</c> —
    /// <c>Accept: text/plain</c>, JSON body with <c>tin</c>/<c>siteId</c>,
    /// <c>Authorization: Bearer {jwt}</c>. Caches the catalog and reconciles local inventory.
    /// </summary>
    public async Task<StockResult<IReadOnlyList<TerminalSiteProductDto>>> GetTerminalSiteProductsAsync(
        GetTerminalSiteProductsRequest request,
        bool reconcileLocalInventory = true,
        bool preserveLocalStock = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Tin);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SiteId);

        var payload = new GetTerminalSiteProductsRequest
        {
            Tin = request.Tin.Trim(),
            SiteId = request.SiteId.Trim()
        };

        var signed = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var context = new MraRequestContext
        {
            JwtToken = signed.JwtToken,
            SecretKey = signed.SecretKey,
            UseBearerAuthorization = true,
            AcceptHeader = "text/plain"
        };

        var response = await _apiClient
            .PostAsync<GetTerminalSiteProductsRequest, IReadOnlyList<TerminalSiteProductDto>>(
                "utilities/get-terminal-site-products",
                payload,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccess)
        {
            _logger.LogWarning(
                "get-terminal-site-products failed for tin={Tin} siteId={SiteId}. Remark={Remark}",
                payload.Tin,
                payload.SiteId,
                response.Remark ?? "(null)");
            return StockResult<IReadOnlyList<TerminalSiteProductDto>>.Failed(response.Remark, response.Errors);
        }

        IReadOnlyList<TerminalSiteProductDto> catalog = response.Data ?? Array.Empty<TerminalSiteProductDto>();
        var result = StockResult<IReadOnlyList<TerminalSiteProductDto>>.Succeeded(catalog, response.Remark);

        var cacheKey = BuildTerminalSiteProductsCacheKey(payload.Tin, payload.SiteId);
        await CacheReferenceDataAsync(cacheKey, catalog, cancellationToken).ConfigureAwait(false);

        if (reconcileLocalInventory)
        {
            var reconciled = await ReconcileTerminalSiteProductsAsync(
                    catalog,
                    preserveLocalStock,
                    cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Cached {Count} MRA site product(s) for tin={Tin} siteId={SiteId}; reconciled={Reconciled}",
                catalog.Count,
                payload.Tin,
                payload.SiteId,
                reconciled);
        }
        else
        {
            _logger.LogInformation(
                "Cached {Count} MRA site product(s) for tin={Tin} siteId={SiteId} (local reconcile skipped)",
                catalog.Count,
                payload.Tin,
                payload.SiteId);
        }

        return result;
    }

    public static string BuildTerminalSiteProductsCacheKey(string tin, string siteId) =>
        MraConfigurationKeys.TerminalSiteProductsCachePrefix
        + tin.Trim()
        + "."
        + siteId.Trim();

    public async Task<int> ReconcileTerminalSiteProductsAsync(
        IReadOnlyList<TerminalSiteProductDto> products,
        bool preserveLocalStock = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(products);
        var upserted = 0;
        var syncedAt = DateTime.UtcNow;

        foreach (var product in products)
        {
            var code = product.ResolveProductCode();
            if (string.IsNullOrWhiteSpace(code))
            {
                _logger.LogWarning("Skipping MRA site product with missing productCode/barcode.");
                continue;
            }

            var name = product.ResolveName();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = code;
            }

            var item = new LocalInventoryItem
            {
                ProductId = code,
                ProductCode = code,
                Name = name,
                UnitPrice = product.Price,
                StockQuantity = product.Quantity,
                UnitOfMeasure = product.UnitOfMeasure?.Trim(),
                TaxRateId = product.TaxRateId?.Trim(),
                HsCode = product.HsCode?.Trim(),
                CatalogSource = "MraEis",
                HeadOfficeRevisionUtc = syncedAt,
                LastReplicatedAtUtc = syncedAt,
                MinReorderQty = product.MinimumStockLevel
            };

            if (preserveLocalStock)
            {
                var existing = await _inventoryRepository
                    .GetByProductCodeAsync(code, cancellationToken)
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
        }

        return upserted;
    }

    public async Task<StockResult<IReadOnlyList<StockAdjustmentReasonDto>>> GetStockAdjustmentReasonsAsync(
        PagedRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var body = request ?? new PagedRequest { PageNumber = 1, PageSize = DefaultPageSize };
        body.PageSize = NormalizePageSize(body.PageSize);

        var response = await _apiClient
            .PostAsync<PagedRequest, IReadOnlyList<StockAdjustmentReasonDto>>(
                "stock/getStockAdjustmentReasons",
                body,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<IReadOnlyList<SupplierDto>>> GetSuppliersAsync(
        PagedRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var body = request ?? new PagedRequest { PageNumber = 1, PageSize = DefaultPageSize };
        body.PageSize = NormalizePageSize(body.PageSize);

        var response = await _apiClient
            .PostAsync<PagedRequest, IReadOnlyList<SupplierDto>>(
                "stock/get-suppliers",
                body,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<StockMutationResponseData>> TransferInventoryAsync(
        TransferInventoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateTransferRequest(request);
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<TransferInventoryRequest, StockMutationResponseData>(
                "stock/transfer-inventory",
                request,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<StockMutationResponseData>> SubmitInformalPurchaseAsync(
        InformalPurchaseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SupplierId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProductCode);
        if (request.Quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Purchase quantity must be greater than zero.");
        }

        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<InformalPurchaseRequest, StockMutationResponseData>(
                "stock/submit-informal-purchase",
                request,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<StockMutationResponseData>> SubmitRawMaterialConversionAsync(
        RawMaterialConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RawMaterialCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProductCode);
        if (request.RawMaterialQuantity <= 0 || request.ProductQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Conversion quantities must be greater than zero.");
        }

        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<RawMaterialConversionRequest, StockMutationResponseData>(
                "raw-material/submit-conversion",
                request,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<StockMutationResponseData>> SubmitStockAdjustmentAsync(
        StockAdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProductCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AdjustmentReasonId);
        if (request.Quantity == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Adjustment quantity cannot be zero.");
        }

        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<StockAdjustmentRequest, StockMutationResponseData>(
                "stock/submit-adjustment",
                request,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<AddProductResponseData>> AddProductAsync(
        AddProductRequest request,
        CancellationToken cancellationToken = default)
    {
        await ValidateAddProductAgainstLocalRulesAsync(request, cancellationToken).ConfigureAwait(false);

        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<AddProductRequest, AddProductResponseData>(
                "stock/add-product",
                request,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        var result = ToResult(response);
        if (result.Success)
        {
            await PersistLocalProductAsync(request, result.Data, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Product {ProductCode} registered with MRA and cached locally.", request.ProductCode);
        }

        return result;
    }

    public int NormalizeInventoryPageSize(int pageSize) =>
        NormalizePageSize(Math.Min(pageSize, _inventoryUploadBatchSize));

    public IReadOnlyList<InventoryUploadBatch<T>> PlanInitialInventoryUploadBatches<T>(IReadOnlyList<T> items) =>
        InventoryUploadBatchPlanner.CreateBatches(items, _inventoryUploadBatchSize);

    public async Task<StockResult<int>> UploadInitialInventoryInBatchesAsync(
        IReadOnlyList<InitialInventoryItemDto> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return StockResult<int>.Succeeded(0, "No inventory items to upload.");
        }

        var batches = InventoryUploadBatchPlanner.CreateBatches(items, _inventoryUploadBatchSize);
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var uploaded = 0;

        foreach (var batch in batches)
        {
            var request = new InitialInventoryUploadBatchRequest
            {
                InventoryItems = batch.Items,
                IsLastBatch = batch.IsLastBatch
            };

            var response = await _apiClient
                .PostAsync<InitialInventoryUploadBatchRequest, InitialInventoryUploadBatchResponseData>(
                    "stock/upload-initial-inventory",
                    request,
                    context,
                    cancellationToken)
                .ConfigureAwait(false);

            var result = ToResult(response);
            if (!result.Success)
            {
                return StockResult<int>.Failed(result.Remark, result.Errors);
            }

            uploaded += batch.Items.Count;
        }

        return StockResult<int>.Succeeded(uploaded, $"Uploaded {uploaded} inventory item(s) in {batches.Count} batch(es).");
    }

    public static int NormalizePageSize(int pageSize) =>
        pageSize switch
        {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize
        };

    private async Task ValidateAddProductAgainstLocalRulesAsync(
        AddProductRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProductCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProductName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HsCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UnitOfMeasure);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TaxRateId);

        if (request.UnitPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unit price cannot be negative.");
        }

        if (request.OpeningStockQuantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Opening stock quantity cannot be negative.");
        }

        var existing = await _inventoryRepository
            .GetByProductCodeAsync(request.ProductCode.Trim(), cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Product code '{request.ProductCode}' already exists in local inventory.");
        }

        await EnsureTaxRateIsConfiguredAsync(request.TaxRateId, cancellationToken).ConfigureAwait(false);
        await EnsureReferenceValueExistsAsync(
            MraConfigurationKeys.StockHsCodesCache,
            request.HsCode,
            value => value.Equals(request.HsCode, StringComparison.OrdinalIgnoreCase),
            "HS code",
            cancellationToken).ConfigureAwait(false);
        await EnsureReferenceValueExistsAsync(
            MraConfigurationKeys.StockUnitsOfMeasureCache,
            request.UnitOfMeasure,
            value => value.Equals(request.UnitOfMeasure, StringComparison.OrdinalIgnoreCase),
            "unit of measure",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureTaxRateIsConfiguredAsync(string taxRateId, CancellationToken cancellationToken)
    {
        var globalJson = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.GlobalConfiguration, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(globalJson))
        {
            throw new InvalidOperationException(
                "Global tax configuration is missing. Call GetLatestConfigs before adding products.");
        }

        var global = JsonSerializer.Deserialize<GlobalConfigurationDto>(globalJson, MraJson.SerializerOptions)
            ?? throw new InvalidOperationException("Global configuration JSON is invalid.");

        var knownRate = global.TaxRates?.Any(rate =>
            string.Equals(rate.Id, taxRateId, StringComparison.OrdinalIgnoreCase)) ?? false;

        if (!knownRate)
        {
            throw new InvalidOperationException(
                $"Tax rate '{taxRateId}' is not present in cached global configuration.");
        }
    }

    private async Task EnsureReferenceValueExistsAsync(
        string cacheKey,
        string submittedValue,
        Func<string, bool> match,
        string label,
        CancellationToken cancellationToken)
    {
        var cacheJson = await _configurationRepository.GetJsonAsync(cacheKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cacheJson))
        {
            throw new InvalidOperationException(
                $"Reference {label} cache is empty. Call the corresponding MRA stock reference endpoint first.");
        }

        if (cacheKey == MraConfigurationKeys.StockHsCodesCache)
        {
            var hsCodes = JsonSerializer.Deserialize<List<HsCodeDto>>(cacheJson, MraJson.SerializerOptions) ?? [];
            if (!hsCodes.Any(x => x.HsCode is not null && match(x.HsCode)))
            {
                throw new InvalidOperationException($"HS code '{submittedValue}' is not in the MRA reference list.");
            }

            return;
        }

        var units = JsonSerializer.Deserialize<List<UnitOfMeasureDto>>(cacheJson, MraJson.SerializerOptions) ?? [];
        if (!units.Any(x => x.Code is not null && match(x.Code)))
        {
            throw new InvalidOperationException(
                $"Unit of measure '{submittedValue}' is not in the MRA reference list.");
        }
    }

    private async Task PersistLocalProductAsync(
        AddProductRequest request,
        AddProductResponseData? responseData,
        CancellationToken cancellationToken)
    {
        var productId = responseData?.ProductId;
        if (string.IsNullOrWhiteSpace(productId))
        {
            productId = request.ProductCode.Trim();
        }

        await _inventoryRepository.UpsertAsync(
            new LocalInventoryItem
            {
                ProductId = productId,
                ProductCode = request.ProductCode.Trim(),
                Name = request.ProductName.Trim(),
                UnitPrice = request.UnitPrice,
                StockQuantity = request.OpeningStockQuantity,
                HsCode = request.HsCode.Trim(),
                UnitOfMeasure = request.UnitOfMeasure.Trim(),
                TaxRateId = request.TaxRateId.Trim()
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task CacheReferenceDataAsync<T>(string key, T payload, CancellationToken cancellationToken)
    {
        await _configurationRepository.UpsertJsonAsync(
            key,
            JsonSerializer.Serialize(payload, MraJson.SerializerOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateTransferRequest(TransferInventoryRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceSiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationSiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ProductCode);
        if (request.Quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Transfer quantity must be greater than zero.");
        }

        if (request.SourceSiteId.Equals(request.DestinationSiteId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source and destination sites must differ.");
        }
    }

    private static StockResult<T> ToResult<T>(EisApiResponse<T> response) =>
        response.IsSuccess && response.Data is not null
            ? StockResult<T>.Succeeded(response.Data, response.Remark)
            : StockResult<T>.Failed(response.Remark, response.Errors);
}

public sealed class StockResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string? Remark { get; init; }
    public IReadOnlyList<EisApiError>? Errors { get; init; }

    public static StockResult<T> Succeeded(T data, string? remark) =>
        new() { Success = true, Data = data, Remark = remark };

    public static StockResult<T> Failed(string? remark, IReadOnlyList<EisApiError>? errors) =>
        new() { Success = false, Remark = remark, Errors = errors };
}
