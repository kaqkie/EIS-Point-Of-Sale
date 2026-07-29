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
using PointOfSale.Mra.Services;
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
    private readonly ITerminalSiteProductsResponseService _siteProductsParser;
    private readonly ITerminalSiteProductsCatalogSyncService _siteProductsSync;
    private readonly ILogger<StockManagementService> _logger;
    private readonly int _inventoryUploadBatchSize;

    public StockManagementService(
        MraApiClient apiClient,
        IMraTerminalAuthProvider authProvider,
        ILocalInventoryRepository inventoryRepository,
        IConfigurationRepository configurationRepository,
        ILogger<StockManagementService> logger,
        IOptions<PosOperationsOptions> posOperations,
        ITerminalSiteProductsResponseService? siteProductsParser = null,
        ITerminalSiteProductsCatalogSyncService? siteProductsSync = null)
    {
        _apiClient = apiClient;
        _authProvider = authProvider;
        _inventoryRepository = inventoryRepository;
        _configurationRepository = configurationRepository;
        _logger = logger;
        _inventoryUploadBatchSize = Math.Clamp(posOperations.Value.InventoryUploadBatchSize, 1, MaxPageSize);
        _siteProductsParser = siteProductsParser
            ?? new TerminalSiteProductsResponseService(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TerminalSiteProductsResponseService>.Instance);
        _siteProductsSync = siteProductsSync
            ?? new TerminalSiteProductsCatalogSyncService(
                _siteProductsParser,
                inventoryRepository,
                configurationRepository,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<TerminalSiteProductsCatalogSyncService>.Instance);
    }

    public async Task<StockResult<PagedResponse<WarehouseInventoryItemDto>>> GetWarehouseInventoryAsync(
        WarehouseInventoryRequest request,
        CancellationToken cancellationToken = default)
    {
        var pageSize = NormalizeInventoryPageSize(request.PageSize);
        var context = await _authProvider.GetJwtContextAsync(cancellationToken).ConfigureAwait(false);
        var query = new Dictionary<string, string>
        {
            ["page"] = Math.Max(1, request.Page).ToString(),
            ["pageSize"] = pageSize.ToString()
        };

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
            ["page"] = Math.Max(1, request.Page).ToString(),
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
        var parsed = _siteProductsParser.Validate(
            new EisApiResponse<IReadOnlyList<TerminalSiteProductDto>>
            {
                StatusCode = response.StatusCode,
                Remark = response.Remark,
                Data = catalog,
                Errors = response.Errors
            });
        if (!parsed.Success)
        {
            return StockResult<IReadOnlyList<TerminalSiteProductDto>>.Failed(parsed.Remark, parsed.Errors);
        }

        var result = StockResult<IReadOnlyList<TerminalSiteProductDto>>.Succeeded(parsed.Products, parsed.Remark);

        if (reconcileLocalInventory)
        {
            var sync = await _siteProductsSync
                .SyncFromSnapshotsAsync(
                    parsed.Snapshots,
                    payload.Tin,
                    payload.SiteId,
                    preserveLocalStock,
                    parsed.Remark,
                    cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Cached/synced {Count} MRA site product(s) for tin={Tin} siteId={SiteId}; upserted={Upserted}",
                parsed.ProductCount,
                payload.Tin,
                payload.SiteId,
                sync.UpsertedCount);
        }
        else
        {
            var cacheKey = BuildTerminalSiteProductsCacheKey(payload.Tin, payload.SiteId);
            await CacheReferenceDataAsync(cacheKey, parsed.Products, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Cached {Count} MRA site product(s) for tin={Tin} siteId={SiteId} (local reconcile skipped)",
                parsed.ProductCount,
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
        var snapshots = _siteProductsParser.BuildCatalogSnapshots(products);
        var sync = await _siteProductsSync
            .SyncFromSnapshotsAsync(
                snapshots,
                tin: null,
                siteId: null,
                preserveLocalStock,
                remark: null,
                cancellationToken)
            .ConfigureAwait(false);
        return sync.UpsertedCount;
    }

    public async Task<StockResult<IReadOnlyList<StockAdjustmentReasonDto>>> GetStockAdjustmentReasonsAsync(
        CancellationToken cancellationToken = default)
    {
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<object, IReadOnlyList<StockAdjustmentReasonDto>>(
                "stock/getStockAdjustmentReasons",
                new { },
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<IReadOnlyList<SupplierDto>>> GetSuppliersAsync(
        CancellationToken cancellationToken = default)
    {
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<object, IReadOnlyList<SupplierDto>>(
                "stock/get-suppliers",
                new { },
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<string>> TransferInventoryAsync(
        TransferInventoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateTransferRequest(request);
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<TransferInventoryRequest, string>(
                "stock/transfer-inventory",
                request,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<string>> SubmitInformalPurchaseAsync(
        InformalPurchaseRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.SupplierId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "SupplierId must be greater than zero.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(request.ReceivedBy);
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ArgumentException("Informal purchase requires at least one item.", nameof(request));
        }

        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<InformalPurchaseRequest, string>(
                "stock/submit-informal-purchase",
                request,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<string>> SubmitRawMaterialConversionAsync(
        RawMaterialConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.RawMaterials is null || request.RawMaterials.Count == 0)
        {
            throw new ArgumentException("Conversion requires at least one raw material.", nameof(request));
        }

        if (request.FinishedProducts is null || request.FinishedProducts.Count == 0)
        {
            throw new ArgumentException("Conversion requires at least one finished product.", nameof(request));
        }

        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<RawMaterialConversionRequest, string>(
                "raw-material/submit-conversion",
                request,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        return ToResult(response);
    }

    public async Task<StockResult<string>> SubmitStockAdjustmentAsync(
        StockAdjustmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Barcode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AdjustmentReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.AdjustmentType);
        if (request.Quantity == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Adjustment quantity cannot be zero.");
        }

        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostAsync<StockAdjustmentRequest, string>(
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
            _logger.LogInformation(
                "Product {ProductCode} registered with MRA and cached locally.",
                request.ResolveProductCode());
        }

        return result;
    }

    public int NormalizeInventoryPageSize(int pageSize) =>
        NormalizePageSize(Math.Min(pageSize, _inventoryUploadBatchSize));

    public static int NormalizePageSize(int pageSize) =>
        pageSize switch
        {
            <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize
        };

    public IReadOnlyList<InventoryUploadBatch<T>> PlanInitialInventoryUploadBatches<T>(IReadOnlyList<T> items) =>
        InventoryUploadBatchPlanner.CreateBatches(items, _inventoryUploadBatchSize);

    public async Task<StockResult<int>> UploadInitialInventoryInBatchesAsync(
        IReadOnlyList<InitialInventoryItemDto> items,
        string? tin = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return StockResult<int>.Succeeded(0, "No inventory items to upload.");
        }

        var taxpayerTin = await ResolveTaxpayerTinAsync(tin, cancellationToken).ConfigureAwait(false);
        var batches = InventoryUploadBatchPlanner.CreateBatches(items, _inventoryUploadBatchSize);
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var uploaded = 0;

        foreach (var batch in batches)
        {
            var request = new InitialInventoryUploadBatchRequest
            {
                Tin = taxpayerTin,
                Products = batch.Items,
                IsLastBatch = batch.IsLastBatch
            };

            var response = await _apiClient
                .PostAsync<InitialInventoryUploadBatchRequest, InitialInventoryUploadBatchResponseData>(
                    "utilities/taxpayer-initial-inventory-upload",
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

    private async Task ValidateAddProductAgainstLocalRulesAsync(
        AddProductRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HsCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Uom);

        if (request.UnitPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Unit price cannot be negative.");
        }

        if (request.OpeningStockQuantity < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Opening stock quantity cannot be negative.");
        }

        var productCode = request.ResolveProductCode();
        var existing = await _inventoryRepository
            .GetByProductCodeAsync(productCode, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Product code '{productCode}' already exists in local inventory.");
        }

        if (!string.IsNullOrWhiteSpace(request.ExpectedTaxRateId))
        {
            await EnsureTaxRateIsConfiguredAsync(request.ExpectedTaxRateId, cancellationToken).ConfigureAwait(false);
        }

        await EnsureReferenceValueExistsAsync(
            MraConfigurationKeys.StockHsCodesCache,
            request.HsCode,
            value => value.Equals(request.HsCode, StringComparison.OrdinalIgnoreCase),
            "HS code",
            cancellationToken).ConfigureAwait(false);
        await EnsureReferenceValueExistsAsync(
            MraConfigurationKeys.StockUnitsOfMeasureCache,
            request.Uom,
            value => value.Equals(request.Uom, StringComparison.OrdinalIgnoreCase),
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
            if (!hsCodes.Any(x =>
            {
                var code = x.ResolveCode();
                return code is not null && match(code);
            }))
            {
                throw new InvalidOperationException($"HS code '{submittedValue}' is not in the MRA reference list.");
            }

            return;
        }

        var units = JsonSerializer.Deserialize<List<UnitOfMeasureDto>>(cacheJson, MraJson.SerializerOptions) ?? [];
        if (!units.Any(x =>
        {
            var code = x.ResolveCode();
            return code is not null && match(code);
        }))
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
        var productCode = FirstNonEmpty(responseData?.Barcode, request.Barcode) ?? request.ResolveProductCode();
        var productId = responseData is { ProductId: > 0 }
            ? responseData.ProductId.ToString()
            : productCode;

        await _inventoryRepository.UpsertAsync(
            new LocalInventoryItem
            {
                ProductId = productId,
                ProductCode = productCode,
                Name = FirstNonEmpty(responseData?.Name, request.Name) ?? productCode,
                UnitPrice = request.UnitPrice,
                StockQuantity = request.OpeningStockQuantity,
                HsCode = FirstNonEmpty(responseData?.HsCode, request.HsCode),
                UnitOfMeasure = FirstNonEmpty(responseData?.Uom, request.Uom),
                TaxRateId = FirstNonEmpty(responseData?.TaxRateId, request.ExpectedTaxRateId)
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

    private async Task<string?> ResolveTaxpayerTinAsync(string? tin, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(tin))
        {
            return tin.Trim();
        }

        var taxpayerJson = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.TaxpayerConfiguration, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(taxpayerJson))
        {
            return null;
        }

        var taxpayer = JsonSerializer.Deserialize<TaxpayerConfigurationDto>(taxpayerJson, MraJson.SerializerOptions);
        return string.IsNullOrWhiteSpace(taxpayer?.Tin) ? null : taxpayer.Tin.Trim();
    }

    private static void ValidateTransferRequest(TransferInventoryRequest request)
    {
        if (!request.FromWarehouseToSite && !request.SiteToWarehouse
            && string.IsNullOrWhiteSpace(request.FromSiteId)
            && string.IsNullOrWhiteSpace(request.ToSiteId))
        {
            throw new ArgumentException(
                "Transfer must specify fromWarehouseToSite, siteToWarehouse, or site ids.",
                nameof(request));
        }

        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ArgumentException("Transfer requires at least one item.", nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.FromSiteId)
            && !string.IsNullOrWhiteSpace(request.ToSiteId)
            && request.FromSiteId.Equals(request.ToSiteId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Source and destination sites must differ.");
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
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
