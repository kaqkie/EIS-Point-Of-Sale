namespace PointOfSale.Core.Entities;

public sealed class GoodsReceiptNote
{
    public long GrnId { get; set; }
    public string GrnNumber { get; set; } = string.Empty;
    public long PoId { get; set; }
    public string PoNumber { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string Status { get; set; } = "Draft";
    public string? DeliveryNoteNumber { get; set; }
    public string? SupplierInvoiceNumber { get; set; }
    public string? OperatorUsername { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }
}

public sealed class GoodsReceiptLine
{
    public long GrnLineId { get; set; }
    public long GrnId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal OrderedQty { get; set; }
    public decimal ReceivedQty { get; set; }
    public decimal DamagedQty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal PreviousStock { get; set; }
    public decimal NewStock { get; set; }
    public decimal PreviousAvgCost { get; set; }
    public decimal NewAvgCost { get; set; }
    public decimal PreviousRetailPrice { get; set; }
    public decimal NewRetailPrice { get; set; }
    public string? LineNotes { get; set; }
}

public sealed class SupplierInvoiceReconciliation
{
    public long ReconciliationId { get; set; }
    public long GrnId { get; set; }
    public string GrnNumber { get; set; } = string.Empty;
    public string SupplierInvoiceNumber { get; set; } = string.Empty;
    public DateTime? InvoiceDate { get; set; }
    public decimal InvoiceTotalMwk { get; set; }
    public decimal ReceivedTotalMwk { get; set; }
    public decimal VarianceMwk { get; set; }
    public string Status { get; set; } = "Pending";
    public string? DiscrepancyNotes { get; set; }
    public string? OperatorUsername { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SignedOffAtUtc { get; set; }
}

public sealed class SupplierInvoiceReconciliationLine
{
    public long ReconciliationLineId { get; set; }
    public long ReconciliationId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string DiscrepancyType { get; set; } = string.Empty;
    public decimal OrderedQty { get; set; }
    public decimal ReceivedQty { get; set; }
    public decimal DamagedQty { get; set; }
    public decimal InvoiceQty { get; set; }
    public decimal UnitCost { get; set; }
    public decimal InvoiceUnitCost { get; set; }
    public string Message { get; set; } = string.Empty;
}
