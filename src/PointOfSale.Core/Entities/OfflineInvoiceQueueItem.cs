namespace PointOfSale.Core.Entities;

public sealed class OfflineInvoiceQueueItem
{
    public int Id { get; set; }
    public required string PayloadJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string Status { get; set; }
    public int RetryCount { get; set; }
    public DateTime? NextRetryTime { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FiscalResponseJson { get; set; }
}
