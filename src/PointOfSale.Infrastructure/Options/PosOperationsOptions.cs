namespace PointOfSale.Infrastructure.Options;

public sealed class PosOperationsOptions
{
    public const string SectionName = "PosOperations";

    /// <summary>Maximum items per inventory upload/sync batch (MRA guidance: 50).</summary>
    public int InventoryUploadBatchSize { get; set; } = 50;

    public int AuditLogRetentionDays { get; set; } = 90;

    public int SyncedInvoiceRetentionDays { get; set; } = 90;
}

public sealed class AuditLoggingOptions
{
    public const string SectionName = "AuditLogging";

    public bool EnableDatabaseAudit { get; set; } = true;

    public bool EnableFileAudit { get; set; } = true;

    public string FileDirectory { get; set; } = "Logs/MraAudit";

    public int RetainedFileDays { get; set; } = 14;
}
