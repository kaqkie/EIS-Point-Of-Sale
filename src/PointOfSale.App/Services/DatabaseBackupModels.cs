namespace PointOfSale.App.Services;

public static class DatabaseBackupTriggers
{
    public const string Manual = "Manual";
    public const string Midnight = "Midnight";
    public const string EndOfShift = "EndOfShift";
    public const string EndOfDay = "EndOfDay";
    public const string Scheduled = "Scheduled";
}

public sealed class DatabaseBackupManifest
{
    public string DatabaseName { get; set; } = string.Empty;
    public string BackupFileName { get; set; } = string.Empty;
    public string BackupFilePath { get; set; } = string.Empty;
    public string ManifestFilePath { get; set; } = string.Empty;
    public string? SecretsSidecarPath { get; set; }
    public string Sha256Checksum { get; set; } = string.Empty;
    public long BackupBytes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Trigger { get; set; } = DatabaseBackupTriggers.Manual;
    public int SchemaVersion { get; set; }
    public bool Compressed { get; set; }
    public bool ChecksumEnabled { get; set; }
    public string? Notes { get; set; }
}

public sealed class DatabaseBackupHistoryEntry
{
    public long BackupId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Trigger { get; set; } = string.Empty;
    public string BackupFilePath { get; set; } = string.Empty;
    public string Sha256Checksum { get; set; } = string.Empty;
    public long BackupBytes { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class DatabaseBackupResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public DatabaseBackupManifest? Manifest { get; init; }
    public string? Message { get; init; }

    public static DatabaseBackupResult Failed(string error) => new() { Success = false, Error = error };

    public static DatabaseBackupResult Ok(DatabaseBackupManifest manifest, string message) =>
        new() { Success = true, Manifest = manifest, Message = message };
}

public sealed class DatabaseRestoreResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public int PreservedQueueItems { get; init; }
    public int RestoredQueueItems { get; init; }
    public bool ChecksumVerified { get; init; }
    public bool VerifyOnlyPassed { get; init; }

    public static DatabaseRestoreResult Failed(string error) => new() { Success = false, Error = error };
}

public sealed class OfflineQueuePreservationBundle
{
    public DateTime CapturedAtUtc { get; set; }
    public string SourceDatabase { get; set; } = string.Empty;
    public IReadOnlyList<PreservedOfflineInvoice> Invoices { get; set; } = Array.Empty<PreservedOfflineInvoice>();
}

public sealed class PreservedOfflineInvoice
{
    public int OriginalId { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public int RetryCount { get; set; }
    public DateTime? NextRetryTime { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FiscalResponseJson { get; set; }
}
