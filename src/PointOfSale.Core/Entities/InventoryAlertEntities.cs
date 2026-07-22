namespace PointOfSale.Core.Entities;

/// <summary>Local vendor / supplier profile for purchase-order grouping.</summary>
public sealed class InventorySupplier
{
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string? ContactEmail { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class InventoryStockAlert
{
    public long AlertId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string AlertType { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public decimal StockQuantity { get; set; }
    public decimal ThresholdQty { get; set; }
    public decimal AverageDailySales { get; set; }
    public string? SupplierCode { get; set; }
    public string Message { get; set; } = string.Empty;
    public bool IsAcknowledged { get; set; }
    public int? ShiftId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? AcknowledgedAtUtc { get; set; }
}

public sealed class PurchaseOrder
{
    public long PoId { get; set; }
    public string PoNumber { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public int LineCount { get; set; }
    public decimal TotalQuantity { get; set; }
    public decimal TotalEstimatedCost { get; set; }
    public string? OperatorUsername { get; set; }
    public string? Notes { get; set; }
    public string? SummaryText { get; set; }
    public DateTime GeneratedAtUtc { get; set; }
    public DateTime? ExportedAtUtc { get; set; }
}

public sealed class PurchaseOrderLine
{
    public long PoLineId { get; set; }
    public long PoId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal CurrentStock { get; set; }
    public decimal MinReorderQty { get; set; }
    public decimal MaxStockCapacity { get; set; }
    public decimal AverageDailySales { get; set; }
    public decimal SuggestedQty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal LineTotal { get; set; }
}
