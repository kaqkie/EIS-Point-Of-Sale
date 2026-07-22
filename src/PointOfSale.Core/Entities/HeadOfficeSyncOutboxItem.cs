namespace PointOfSale.Core.Entities;

public static class HeadOfficeSyncPayloadTypes
{
    public const string SalesSummary = "SalesSummary";
    public const string ZReport = "ZReport";
    public const string InventoryAdjustment = "InventoryAdjustment";
    public const string FinancialClosure = "FinancialClosure";
}

public static class HeadOfficeSyncOutboxStatuses
{
    public const string Pending = "Pending";
    public const string Uploading = "Uploading";
    public const string Uploaded = "Uploaded";
    public const string Failed = "Failed";
}

public sealed class HeadOfficeSyncOutboxItem
{
    public long OutboxId { get; set; }
    public string PayloadType { get; set; } = string.Empty;
    public string CorrelationKey { get; set; } = string.Empty;
    public string PlainJson { get; set; } = string.Empty;
    public string Status { get; set; } = HeadOfficeSyncOutboxStatuses.Pending;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UploadedAtUtc { get; set; }
    public string? ErrorMessage { get; set; }
    public int AttemptCount { get; set; }
}
