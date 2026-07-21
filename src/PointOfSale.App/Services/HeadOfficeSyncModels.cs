namespace PointOfSale.App.Services;

public sealed class HeadOfficeBranchSyncPackage
{
    public required string BranchId { get; init; }
    public required string TerminalId { get; init; }
    public DateTime PackagedAtUtc { get; init; }
    public required string PayloadType { get; init; }
    public required string CorrelationKey { get; init; }
    public required EncryptedHeadOfficeEnvelope EncryptedPayload { get; init; }
}

public sealed class HeadOfficeUploadBatchRequest
{
    public required string BranchId { get; init; }
    public required string TerminalId { get; init; }
    public DateTime SentAtUtc { get; init; }
    public IReadOnlyList<HeadOfficeBranchSyncPackage> Packages { get; init; } = Array.Empty<HeadOfficeBranchSyncPackage>();
}

public sealed class HeadOfficeUploadBatchResponse
{
    public bool Accepted { get; set; }
    public IReadOnlyList<string> AcceptedCorrelationKeys { get; set; } = Array.Empty<string>();
    public string? Message { get; set; }
}

public sealed class CentralCatalogDeltaResponse
{
    public DateTime GeneratedAtUtc { get; set; }
    public DateTime CatalogRevisionUtc { get; set; }
    public IReadOnlyList<CentralCatalogProduct> Products { get; set; } = Array.Empty<CentralCatalogProduct>();
}

public sealed class CentralCatalogProduct
{
    public required string ProductId { get; set; }
    public required string ProductCode { get; set; }
    public required string Name { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal? StockQuantity { get; set; }
    public string? HsCode { get; set; }
    public string? UnitOfMeasure { get; set; }
    public string? TaxRateId { get; set; }
    public bool OverrideLocalStock { get; set; }
    public DateTime RevisionUtc { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class HeadOfficeSyncResult
{
    public bool Enabled { get; init; }
    public bool Success { get; init; }
    public bool SkippedOffline { get; init; }
    public int PackagedCount { get; init; }
    public int UploadedCount { get; init; }
    public int CatalogProductsApplied { get; init; }
    public int ConflictsPreservedLocalStock { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }

    public static HeadOfficeSyncResult Disabled(string message) =>
        new() { Enabled = false, Success = true, Message = message };

    public static HeadOfficeSyncResult Offline(string message) =>
        new() { Enabled = true, SkippedOffline = true, Success = true, Message = message };

    public static HeadOfficeSyncResult Failed(string error) =>
        new() { Enabled = true, Success = false, Error = error, CompletedAtUtc = DateTime.UtcNow };
}

public sealed class HeadOfficeSyncStatusSnapshot
{
    public bool Enabled { get; init; }
    public bool IsSyncing { get; init; }
    public bool IsHeadOfficeReachable { get; init; }
    public bool IsNetworkAvailable { get; init; }
    public DateTime? LastSyncTimestampUtc { get; init; }
    public DateTime? LastCatalogPullUtc { get; init; }
    public int PendingUploadCount { get; init; }
    public int FailedUploadCount { get; init; }
    public string ConnectionStatusText { get; init; } = string.Empty;
    public string? LastError { get; init; }
}
