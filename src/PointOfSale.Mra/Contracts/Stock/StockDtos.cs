using System.Text.Json.Serialization;

namespace PointOfSale.Mra.Contracts.Stock;

public sealed class PagedRequest
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 50;
}

public sealed class WarehouseInventoryRequest
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 50;

    [JsonPropertyName("siteId")]
    public string? SiteId { get; set; }

    [JsonPropertyName("warehouseId")]
    public string? WarehouseId { get; set; }
}

public sealed class PagedResponse<T>
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalRecords")]
    public int TotalRecords { get; set; }

    [JsonPropertyName("items")]
    public IReadOnlyList<T>? Items { get; set; }

    [JsonPropertyName("data")]
    public IReadOnlyList<T>? Data { get; set; }

    public IReadOnlyList<T> GetItems() => Items ?? Data ?? Array.Empty<T>();
}

public sealed class WarehouseInventoryItemDto
{
    [JsonPropertyName("productId")]
    public string? ProductId { get; set; }

    [JsonPropertyName("productCode")]
    public string? ProductCode { get; set; }

    [JsonPropertyName("productName")]
    public string? ProductName { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("hsCode")]
    public string? HsCode { get; set; }

    [JsonPropertyName("unitOfMeasure")]
    public string? UnitOfMeasure { get; set; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; set; }

    [JsonPropertyName("quantityOnHand")]
    public decimal QuantityOnHand { get; set; }

    [JsonPropertyName("stockQuantity")]
    public decimal StockQuantity { get; set; }

    [JsonPropertyName("taxRateId")]
    public string? TaxRateId { get; set; }

    public string ResolveName() => ProductName ?? Name ?? ProductCode ?? string.Empty;

    public decimal ResolveQuantity() => QuantityOnHand != 0 ? QuantityOnHand : StockQuantity;
}

public sealed class RawMaterialRequest
{
    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; } = 1;

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 50;
}

public sealed class RawMaterialItemDto
{
    [JsonPropertyName("rawMaterialId")]
    public string? RawMaterialId { get; set; }

    [JsonPropertyName("rawMaterialCode")]
    public string? RawMaterialCode { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("unitOfMeasure")]
    public string? UnitOfMeasure { get; set; }

    [JsonPropertyName("quantityOnHand")]
    public decimal QuantityOnHand { get; set; }
}

public sealed class HsCodeDto
{
    [JsonPropertyName("hsCode")]
    public string? HsCode { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class UnitOfMeasureDto
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class StockAdjustmentReasonDto
{
    [JsonPropertyName("reasonId")]
    public string? ReasonId { get; set; }

    [JsonPropertyName("reasonCode")]
    public string? ReasonCode { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

public sealed class SupplierDto
{
    [JsonPropertyName("supplierId")]
    public string? SupplierId { get; set; }

    [JsonPropertyName("supplierName")]
    public string? SupplierName { get; set; }

    [JsonPropertyName("tin")]
    public string? Tin { get; set; }
}

public sealed class TransferInventoryRequest
{
    [JsonPropertyName("sourceSiteId")]
    public required string SourceSiteId { get; init; }

    [JsonPropertyName("destinationSiteId")]
    public required string DestinationSiteId { get; init; }

    [JsonPropertyName("productCode")]
    public required string ProductCode { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("transferReference")]
    public string? TransferReference { get; init; }
}

public sealed class InformalPurchaseRequest
{
    [JsonPropertyName("supplierId")]
    public required string SupplierId { get; init; }

    [JsonPropertyName("purchaseDate")]
    public DateTime PurchaseDate { get; init; }

    [JsonPropertyName("productCode")]
    public required string ProductCode { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; init; }

    [JsonPropertyName("referenceNumber")]
    public string? ReferenceNumber { get; init; }
}

public sealed class RawMaterialConversionRequest
{
    [JsonPropertyName("rawMaterialCode")]
    public required string RawMaterialCode { get; init; }

    [JsonPropertyName("productCode")]
    public required string ProductCode { get; init; }

    [JsonPropertyName("rawMaterialQuantity")]
    public decimal RawMaterialQuantity { get; init; }

    [JsonPropertyName("productQuantity")]
    public decimal ProductQuantity { get; init; }

    [JsonPropertyName("conversionReference")]
    public string? ConversionReference { get; init; }
}

public sealed class StockAdjustmentRequest
{
    [JsonPropertyName("productCode")]
    public required string ProductCode { get; init; }

    [JsonPropertyName("adjustmentReasonId")]
    public required string AdjustmentReasonId { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("adjustmentDate")]
    public DateTime AdjustmentDate { get; init; }

    [JsonPropertyName("remarks")]
    public string? Remarks { get; init; }
}

public sealed class AddProductRequest
{
    [JsonPropertyName("productCode")]
    public required string ProductCode { get; init; }

    [JsonPropertyName("productName")]
    public required string ProductName { get; init; }

    [JsonPropertyName("hsCode")]
    public required string HsCode { get; init; }

    [JsonPropertyName("unitOfMeasure")]
    public required string UnitOfMeasure { get; init; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; init; }

    [JsonPropertyName("taxRateId")]
    public required string TaxRateId { get; init; }

    [JsonPropertyName("isProduct")]
    public bool IsProduct { get; init; } = true;

    [JsonPropertyName("openingStockQuantity")]
    public decimal OpeningStockQuantity { get; init; }

    [JsonPropertyName("siteId")]
    public string? SiteId { get; init; }
}

public sealed class AddProductResponseData
{
    [JsonPropertyName("productId")]
    public string? ProductId { get; set; }

    [JsonPropertyName("productCode")]
    public string? ProductCode { get; set; }
}

public sealed class InitialInventoryItemDto
{
    [JsonPropertyName("productCode")]
    public required string ProductCode { get; init; }

    [JsonPropertyName("productName")]
    public required string ProductName { get; init; }

    [JsonPropertyName("unitPrice")]
    public decimal UnitPrice { get; init; }

    [JsonPropertyName("openingStockQuantity")]
    public decimal OpeningStockQuantity { get; init; }

    [JsonPropertyName("taxRateId")]
    public required string TaxRateId { get; init; }
}

public sealed class InitialInventoryUploadBatchRequest
{
    [JsonPropertyName("inventoryItems")]
    public required IReadOnlyList<InitialInventoryItemDto> InventoryItems { get; init; }

    [JsonPropertyName("isLastBatch")]
    public bool IsLastBatch { get; init; }
}

public sealed class InitialInventoryUploadBatchResponseData
{
    [JsonPropertyName("acceptedCount")]
    public int AcceptedCount { get; set; }
}

public sealed class StockMutationResponseData
{
    [JsonPropertyName("transactionId")]
    public string? TransactionId { get; set; }

    [JsonPropertyName("referenceNumber")]
    public string? ReferenceNumber { get; set; }
}
