namespace PointOfSale.Core.Entities;

public sealed class FiscalYearArchiveRecord
{
    public long ArchiveId { get; set; }
    public int FiscalYear { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime RolledOverAtUtc { get; set; }
    public string PrimarySupervisorUsername { get; set; } = string.Empty;
    public string SecondarySupervisorUsername { get; set; } = string.Empty;
    public decimal TotalGrossSalesMwk { get; set; }
    public decimal TotalVatCollectedMwk { get; set; }
    public int ExpectedClosureDays { get; set; }
    public int ClosedDays { get; set; }
    public int SyncedInvoiceCount { get; set; }
    public string ManifestSha256 { get; set; } = string.Empty;
    public string ManifestHmacSha512 { get; set; } = string.Empty;
    public string ArchiveFilePath { get; set; } = string.Empty;
    public long ArchiveBytes { get; set; }
    public bool CryptographicVerificationPassed { get; set; }
    public string Status { get; set; } = FiscalYearArchiveStatuses.Locked;
    public string? Notes { get; set; }
}

public sealed class FiscalArchivePackageRecord
{
    public long PackageId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string PackageType { get; set; } = string.Empty;
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public long FileBytes { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string TriggeredByUsername { get; set; } = string.Empty;
    public bool DualKeyProtected { get; set; }
}

public static class FiscalYearArchiveStatuses
{
    public const string Locked = "Locked";
    public const string Failed = "Failed";
}

public static class FiscalArchivePackageTypes
{
    public const string FiscalYearRollover = "FiscalYearRollover";
    public const string StaleDataCompression = "StaleDataCompression";
}

public sealed class FiscalYearRolloverPreview
{
    public int FiscalYear { get; init; }
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public int ExpectedBusinessDays { get; init; }
    public int ClosedDays { get; init; }
    public IReadOnlyList<DateTime> MissingClosureDates { get; init; } = Array.Empty<DateTime>();
    public decimal TotalGrossSalesMwk { get; init; }
    public decimal TotalVatCollectedMwk { get; init; }
    public decimal CumulativeGrossAtYearEnd { get; init; }
    public decimal CumulativeVatAtYearEnd { get; init; }
    public int SyncedInvoiceCount { get; init; }
    public int SignatureRowsVerified { get; init; }
    public bool CanRollover { get; init; }
    public string SummaryMessage { get; init; } = string.Empty;
}

public sealed class FiscalYearRolloverResult
{
    public required FiscalYearArchiveRecord Record { get; init; }
    public string ArchiveFilePath { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class ArchivalCompressionResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? PackagePath { get; init; }
    public long BytesWritten { get; init; }
    public int SalesRowsArchived { get; init; }
    public int TelemetryRowsArchived { get; init; }
}
