namespace PointOfSale.App.Options;

/// <summary>Goods receipt and supplier invoice reconciliation defaults.</summary>
public sealed class GoodsReceiptOptions
{
    public const string SectionName = "GoodsReceipt";

    /// <summary>When true, retail UnitPrice is recalculated from WAC × (1 + markup%).</summary>
    public bool ApplyRetailMarkupOnReceipt { get; set; } = true;

    /// <summary>Default markup % when LocalInventory.MarkupPercent is 0.</summary>
    public decimal DefaultMarkupPercent { get; set; } = 25m;

    /// <summary>Absolute MWK variance between invoice and received value that triggers a flag.</summary>
    public decimal InvoiceVarianceToleranceMwk { get; set; } = 1m;

    /// <summary>Quantity variance (ordered vs received) above this is a short/over delivery.</summary>
    public decimal QuantityVarianceTolerance { get; set; } = 0.01m;
}

public static class GoodsReceiptStatuses
{
    public const string Draft = "Draft";
    public const string Posted = "Posted";
    public const string Cancelled = "Cancelled";
}

public static class SupplierReconciliationStatuses
{
    public const string Pending = "Pending";
    public const string Discrepancy = "Discrepancy";
    public const string Matched = "Matched";
    public const string SignedOff = "SignedOff";
}

public static class SupplierDiscrepancyTypes
{
    public const string ShortDelivery = "ShortDelivery";
    public const string OverDelivery = "OverDelivery";
    public const string DamagedStock = "DamagedStock";
    public const string CostVariance = "CostVariance";
    public const string InvoiceTotalVariance = "InvoiceTotalVariance";
}
