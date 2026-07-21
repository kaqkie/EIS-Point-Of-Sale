namespace PointOfSale.Mra.Domain.Entities;

public sealed class TerminalEntity
{
    public required string TerminalId { get; init; }
    public string? TerminalActivationCode { get; set; }
    public DateTime? ActivationDateUtc { get; set; }
    public byte ActivationStatus { get; set; }
    public string? JwtToken { get; set; }
    public string? SecretKey { get; set; }
    public string? ApiKey { get; set; }
    public string? Tin { get; set; }
    public int GlobalConfigVersion { get; set; }
    public int TerminalConfigVersion { get; set; }
    public int TaxpayerConfigVersion { get; set; }
    public string? ProductId { get; set; }
    public string? ProductVersion { get; set; }
    public string? PlatformOsName { get; set; }
    public string? PlatformOsVersion { get; set; }
    public string? PlatformOsBuild { get; set; }
    public string? PlatformMacAddress { get; set; }
    public string ApiEnvironment { get; set; } = "Dev";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class ConfigurationEntity
{
    public long ConfigurationId { get; init; }
    public required string TerminalId { get; init; }
    public byte ConfigScope { get; set; }
    public int VersionNo { get; set; }
    public required string PayloadJson { get; set; }
    public required string Source { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
}

public sealed class OfflineInvoiceQueueEntity
{
    public long QueueId { get; init; }
    public required string TerminalId { get; init; }
    public long FifoSequence { get; set; }
    public required string InvoiceNumber { get; set; }
    public DateTime InvoiceDateTimeUtc { get; set; }
    public required string PayloadJson { get; set; }
    public string? OfflineSignature { get; set; }
    public byte QueueStatus { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public bool IsQuarantined { get; set; }
    public string? QuarantineReason { get; set; }
    public DateTime? QuarantinedAtUtc { get; set; }
    public DateTime? QuarantineReleasedAtUtc { get; set; }
    public string? QuarantineReleasedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LockedAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
}

public sealed class LocalInventoryEntity
{
    public long LocalInventoryId { get; init; }
    public required string TerminalId { get; init; }
    public required string ProductCode { get; set; }
    public required string Description { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal QuantityOnHand { get; set; }
    public required string TaxRateId { get; set; }
    public bool IsProduct { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime LastModifiedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
