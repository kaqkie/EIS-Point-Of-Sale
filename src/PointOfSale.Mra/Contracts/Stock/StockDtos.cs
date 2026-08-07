using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Contracts.Stock;

/// <summary>Local paging helper for warehouse/raw-material GETs (query: <c>page</c>, <c>pageSize</c>).</summary>
public sealed class PagedRequest
{
    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;
}

public sealed class WarehouseInventoryRequest
{
    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;
}

/// <summary>Maps EIS warehouse/raw-material inventory page wrappers.</summary>
public sealed class PagedResponse<T>
{
    [JsonPropertyName("stocks")]
    public IReadOnlyList<T>? Stocks { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    /// <summary>Legacy alias accepted when deserializing older cached payloads.</summary>
    [JsonPropertyName("items")]
    public IReadOnlyList<T>? Items { get; set; }

    [JsonPropertyName("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    public IReadOnlyList<T> GetItems() => Stocks ?? Items ?? Array.Empty<T>();

    public int ResolveTotal() => Total != 0 ? Total : TotalRecords;

    public int ResolvePage() => Page != 0 ? Page : PageNumber;
}

/// <summary>Maps EIS <c>WarehouseInventoryItemDto</c>.</summary>
public sealed class WarehouseInventoryItemDto
{
    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; set; }

    [JsonPropertyName("productDescription")]
    public string? ProductDescription { get; set; }

    [JsonPropertyName("currentQuantity")]
    public decimal? CurrentQuantity { get; set; }

    [JsonPropertyName("uom")]
    public string? Uom { get; set; }

    [JsonPropertyName("price")]
    public decimal? Price { get; set; }

    public string ResolveProductCode() => Barcode?.Trim() ?? string.Empty;

    public string ResolveName() =>
        FirstNonEmpty(ProductName, ProductDescription, Barcode) ?? string.Empty;

    public decimal ResolveQuantity() => CurrentQuantity ?? 0m;

    public decimal ResolveUnitPrice() => Price ?? 0m;

    public bool HasUnitPrice => Price is > 0m;

    public string? ResolveUnitOfMeasure() => Uom;

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
}

public sealed class RawMaterialRequest
{
    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = 50;
}

/// <summary>Maps EIS <c>TaxpayerRawMaterialDto</c>.</summary>
public sealed class RawMaterialItemDto
{
    [JsonPropertyName("rawMaterialName")]
    public string? RawMaterialName { get; set; }

    [JsonPropertyName("rawMaterialDescription")]
    public string? RawMaterialDescription { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("currentQuantity")]
    public decimal CurrentQuantity { get; set; }

    [JsonPropertyName("uom")]
    public string? Uom { get; set; }
}

/// <summary>Maps EIS <c>HsCodeLookupDto</c>.</summary>
public sealed class HsCodeDto
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("taxRateId")]
    public string? TaxRateId { get; set; }

    /// <summary>Legacy alias for older cache payloads.</summary>
    [JsonPropertyName("hsCode")]
    public string? HsCode { get; set; }

    public string? ResolveCode() => FirstNonEmpty(Code, HsCode);

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
}

/// <summary>Maps EIS <c>UnitOfMeasureDto</c>.</summary>
public sealed class UnitOfMeasureDto
{
    [JsonPropertyName("unitOfMeasure")]
    public string? UnitOfMeasure { get; set; }

    [JsonPropertyName("unitOfMeasureDescription")]
    public string? UnitOfMeasureDescription { get; set; }

    /// <summary>Legacy aliases for older cache payloads.</summary>
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    public string? ResolveCode() => FirstNonEmpty(UnitOfMeasure, Code);

    public string? ResolveName() => FirstNonEmpty(UnitOfMeasureDescription, Name, UnitOfMeasure, Code);

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
}

/// <summary>Maps EIS <c>AdjustmentReasonDto</c>.</summary>
public sealed class StockAdjustmentReasonDto
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

/// <summary>Maps EIS <c>SupplierDto</c>.</summary>
public sealed class SupplierDto
{
    [JsonPropertyName("supplierId")]
    public int SupplierId { get; set; }

    [JsonPropertyName("supplierName")]
    public string? SupplierName { get; set; }

    [JsonPropertyName("supplierContactEmail")]
    public string? SupplierContactEmail { get; set; }

    [JsonPropertyName("supplierContactPhone")]
    public string? SupplierContactPhone { get; set; }

    [JsonPropertyName("supplierTin")]
    public string? SupplierTin { get; set; }
}

/// <summary>Maps EIS <c>InventoryTransferRequest</c>.</summary>
public sealed class TransferInventoryRequest
{
    [JsonPropertyName("fromWarehouseToSite")]
    public bool FromWarehouseToSite { get; init; }

    [JsonPropertyName("siteToWarehouse")]
    public bool SiteToWarehouse { get; init; }

    [JsonPropertyName("fromSiteId")]
    public string? FromSiteId { get; init; }

    [JsonPropertyName("toSiteId")]
    public string? ToSiteId { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<InventoryTransferItemDto> Items { get; init; }
}

/// <summary>Maps EIS <c>InventoryTransferItem</c>.</summary>
public sealed class InventoryTransferItemDto
{
    [JsonPropertyName("barcode")]
    public required string Barcode { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("price")]
    public decimal Price { get; init; }
}

/// <summary>Maps EIS <c>GoodsReceivingModel</c> (informal purchase).</summary>
public sealed class InformalPurchaseRequest
{
    [JsonPropertyName("supplierId")]
    public int SupplierId { get; init; }

    [JsonPropertyName("deliveryNoteNumber")]
    public string? DeliveryNoteNumber { get; init; }

    [JsonPropertyName("receivingDate")]
    public DateTime ReceivingDate { get; init; }

    [JsonPropertyName("purchaseOrderNumber")]
    public string? PurchaseOrderNumber { get; init; }

    [JsonPropertyName("receivedBy")]
    public required string ReceivedBy { get; init; }

    [JsonPropertyName("totalItems")]
    public int TotalItems { get; init; }

    [JsonPropertyName("totalQuantity")]
    public decimal TotalQuantity { get; init; }

    [JsonPropertyName("totalValue")]
    public decimal TotalValue { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }

    [JsonPropertyName("items")]
    public required IReadOnlyList<InformalPurchaseItemDto> Items { get; init; }
}

/// <summary>Maps EIS <c>GoodsReceivingItemModel</c>.</summary>
public sealed class InformalPurchaseItemDto
{
    [JsonPropertyName("itemCode")]
    public required string ItemCode { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("quantityOrdered")]
    public decimal QuantityOrdered { get; init; }

    [JsonPropertyName("quantityReceived")]
    public decimal QuantityReceived { get; init; }

    [JsonPropertyName("unitOfMeasure")]
    public required string UnitOfMeasure { get; init; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; init; }

    [JsonPropertyName("totalPrice")]
    public decimal TotalPrice { get; init; }

    [JsonPropertyName("isFinishedProduct")]
    public bool IsFinishedProduct { get; init; } = true;
}

/// <summary>Maps EIS <c>RawmaterialConversion</c>.</summary>
public sealed class RawMaterialConversionRequest
{
    [JsonPropertyName("productionBatchId")]
    public string? ProductionBatchId { get; init; }

    [JsonPropertyName("productionDate")]
    public DateTime? ProductionDate { get; init; }

    [JsonPropertyName("rawMaterials")]
    public required IReadOnlyList<RawMaterialConversionInputDto> RawMaterials { get; init; }

    [JsonPropertyName("finishedProducts")]
    public required IReadOnlyList<FinishedProductionDto> FinishedProducts { get; init; }
}

/// <summary>Maps EIS <c>RawMaterial</c>.</summary>
public sealed class RawMaterialConversionInputDto
{
    [JsonPropertyName("productId")]
    public required string ProductId { get; init; }

    [JsonPropertyName("productName")]
    public required string ProductName { get; init; }

    [JsonPropertyName("availableQuantity")]
    public decimal AvailableQuantity { get; init; }

    [JsonPropertyName("usedQuantity")]
    public decimal UsedQuantity { get; init; }
}

/// <summary>Maps EIS <c>FinishedProduction</c>.</summary>
public sealed class FinishedProductionDto
{
    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("unitOfMeasure")]
    public required string UnitOfMeasure { get; init; }

    [JsonPropertyName("expiryDate")]
    public DateTime? ExpiryDate { get; init; }

    [JsonPropertyName("productDescription")]
    public required string ProductDescription { get; init; }

    [JsonPropertyName("barcode")]
    public required string Barcode { get; init; }
}

/// <summary>Maps EIS <c>StockAdjustmentRequestDto</c>.</summary>
public sealed class StockAdjustmentRequest
{
    [JsonPropertyName("barcode")]
    public required string Barcode { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("adjustmentReason")]
    public required string AdjustmentReason { get; init; }

    /// <summary>EIS values: <c>Increase</c> or <c>Decrease</c>.</summary>
    [JsonPropertyName("adjustmentType")]
    public required string AdjustmentType { get; init; }

    [JsonPropertyName("siteId")]
    public string? SiteId { get; init; }

    [JsonPropertyName("taxpayerRemarks")]
    public string? TaxpayerRemarks { get; init; }
}

/// <summary>Maps EIS <c>AddProductApiRequest</c>.</summary>
public sealed class AddProductRequest
{
    [JsonPropertyName("barcode")]
    public string? Barcode { get; init; }

    [JsonPropertyName("hsCode")]
    public required string HsCode { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("uom")]
    public required string Uom { get; init; }

    /// <summary>Local-only fields used when caching the product after a successful add.</summary>
    [JsonIgnore]
    public decimal UnitPrice { get; init; }

    [JsonIgnore]
    public decimal OpeningStockQuantity { get; init; }

    [JsonIgnore]
    public string? ExpectedTaxRateId { get; init; }

    [JsonIgnore]
    public string? SiteId { get; init; }

    public string ResolveProductCode() =>
        string.IsNullOrWhiteSpace(Barcode) ? Name.Trim() : Barcode.Trim();
}

/// <summary>Maps EIS <c>AddProductApiResponseDto</c>.</summary>
public sealed class AddProductResponseData
{
    [JsonPropertyName("productId")]
    public long ProductId { get; set; }

    [JsonPropertyName("barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("hsCode")]
    public string? HsCode { get; set; }

    [JsonPropertyName("taxRateId")]
    public string? TaxRateId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("uom")]
    public string? Uom { get; set; }
}

/// <summary>Maps EIS <c>InventoryItem</c> for initial inventory upload.</summary>
public sealed class InitialInventoryItemDto
{
    [JsonPropertyName("barCode")]
    public required string BarCode { get; init; }

    [JsonPropertyName("productName")]
    public required string ProductName { get; init; }

    [JsonPropertyName("productDescription")]
    public required string ProductDescription { get; init; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; init; }

    [JsonPropertyName("quantityInStock")]
    public decimal QuantityInStock { get; init; }

    [JsonPropertyName("costPrice")]
    public decimal CostPrice { get; init; }

    [JsonPropertyName("sellingPrice")]
    public decimal SellingPrice { get; init; }

    [JsonPropertyName("reorderLevel")]
    public decimal? ReorderLevel { get; init; }

    [JsonPropertyName("overQuantityStockLevel")]
    public decimal? OverQuantityStockLevel { get; init; }
}

/// <summary>Maps EIS <c>TaxpayerInitialInventoryUploadRequest</c>.</summary>
public sealed class InitialInventoryUploadBatchRequest
{
    [JsonPropertyName("tin")]
    public string? Tin { get; init; }

    [JsonPropertyName("isLastBatch")]
    public bool IsLastBatch { get; init; }

    [JsonPropertyName("products")]
    public required IReadOnlyList<InitialInventoryItemDto> Products { get; init; }
}

/// <summary>Maps EIS <c>InitialInventoryResponse</c>.</summary>
public sealed class InitialInventoryUploadBatchResponseData
{
    [JsonPropertyName("totalItems")]
    public int TotalItems { get; set; }

    [JsonPropertyName("mappedItems")]
    public int MappedItems { get; set; }

    [JsonPropertyName("unmappedItems")]
    public int UnmappedItems { get; set; }

    [JsonPropertyName("isPartialUpload")]
    public bool IsPartialUpload { get; set; }

    [JsonPropertyName("skippedItems")]
    public IReadOnlyList<string>? SkippedItems { get; set; }
}
