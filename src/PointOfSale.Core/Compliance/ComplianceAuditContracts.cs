namespace PointOfSale.Core.Compliance;

public static class ComplianceAuditCategories
{
    public const string TransactionSubmission = "TransactionSubmission";
    public const string OfflineQueue = "OfflineQueue";
    public const string VatOverride = "VatOverride";
    public const string SupervisorAuth = "SupervisorAuth";
    public const string MraHandshake = "MraHandshake";
    public const string Certificate = "Certificate";
}

public sealed class ComplianceTamperCheckResult
{
    public bool IsValid { get; init; }
    public long EntriesVerified { get; init; }
    public string Message { get; init; } = string.Empty;
    public long? FirstBrokenEntryId { get; init; }
}

public sealed class ComplianceAuditLogEntry
{
    public long EntryId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string OperatorUsername { get; set; } = string.Empty;
    public string? CorrelationId { get; set; }
    public string Detail { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string PreviousHash { get; set; } = string.Empty;
    public string EntryHash { get; set; } = string.Empty;
}

public interface IComplianceAuditLogger
{
    Task LogEventAsync(
        string category,
        string action,
        string detail,
        bool success = true,
        string? correlationId = null,
        string? operatorUsername = null,
        CancellationToken cancellationToken = default);

    Task<ComplianceTamperCheckResult> VerifyChainAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ComplianceAuditLogEntry>> GetRecentAsync(
        int take = 100,
        CancellationToken cancellationToken = default);
}
