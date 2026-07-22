namespace PointOfSale.App.Options;

/// <summary>
/// Real-time stock alert thresholds and purchase-order restock defaults.
/// </summary>
public sealed class InventoryAlertOptions
{
    public const string SectionName = "InventoryAlerts";

    public bool Enabled { get; set; } = true;

    /// <summary>Background scan interval in seconds.</summary>
    public int ScanIntervalSeconds { get; set; } = 60;

    /// <summary>Default minimum reorder when LocalInventory.MinReorderQty is 0.</summary>
    public decimal DefaultMinReorderQty { get; set; } = 5m;

    /// <summary>Default max capacity when LocalInventory.MaxStockCapacity is 0 (0 = uncapped).</summary>
    public decimal DefaultMaxStockCapacity { get; set; } = 0m;

    /// <summary>Units sold / day above this rate → FastMoving alert.</summary>
    public decimal FastMovingDailyUnits { get; set; } = 20m;

    /// <summary>Sales lookback window for velocity (days).</summary>
    public int VelocityLookbackDays { get; set; } = 14;

    /// <summary>Target days of cover when calculating suggested PO qty.</summary>
    public int TargetDaysOfCover { get; set; } = 14;

    /// <summary>Fallback supplier code when product has none assigned.</summary>
    public string DefaultSupplierCode { get; set; } = "UNASSIGNED";

    public string DefaultSupplierName { get; set; } = "Unassigned Supplier";
}

public static class InventoryAlertTypes
{
    public const string LowStock = "LowStock";
    public const string Stockout = "Stockout";
    public const string Overstock = "Overstock";
    public const string FastMoving = "FastMoving";

    public static readonly string[] All = [LowStock, Stockout, Overstock, FastMoving];
}

public static class InventoryAlertSeverities
{
    public const string Info = "Info";
    public const string Warning = "Warning";
    public const string Critical = "Critical";
}

public static class PurchaseOrderStatuses
{
    public const string Draft = "Draft";
    public const string ReadyForSignOff = "ReadyForSignOff";
    public const string Exported = "Exported";
    public const string PartiallyReceived = "PartiallyReceived";
    public const string Received = "Received";
    public const string Cancelled = "Cancelled";
}
