namespace PointOfSale.App.Options;

/// <summary>
/// SQL Express disaster-recovery backup settings for Albert Retail Terminal.
/// </summary>
public sealed class DatabaseBackupOptions
{
    public const string SectionName = "DatabaseBackup";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Folder for .bak snapshots and manifests. Empty = %ProgramData%\AlbertRetailTerminal\Backups.
    /// Must be writable by both the POS process and the SQL Server service account.
    /// </summary>
    public string BackupDirectory { get; set; } = string.Empty;

    /// <summary>Rolling retention of successful full backups (files + history rows).</summary>
    public int RetentionCount { get; set; } = 14;

    /// <summary>Run an automatic backup shortly after midnight (local time).</summary>
    public bool BackupAtMidnight { get; set; } = true;

    /// <summary>Run an automatic backup when a cashier shift is closed.</summary>
    public bool BackupOnShiftClose { get; set; } = true;

    /// <summary>Prefer WITH COMPRESSION on BACKUP DATABASE (falls back if unsupported).</summary>
    public bool UseCompression { get; set; } = true;

    /// <summary>Include WITH CHECKSUM on BACKUP / VERIFYONLY.</summary>
    public bool UseChecksum { get; set; } = true;

    /// <summary>How often the background scheduler wakes (minutes).</summary>
    public int SchedulerPollMinutes { get; set; } = 5;

    /// <summary>SQL command timeout for BACKUP/RESTORE (seconds).</summary>
    public int CommandTimeoutSeconds { get; set; } = 600;

    /// <summary>Configuration keys whose JSON is DPAPI-exported beside each .bak (tokens/secrets).</summary>
    public string[] SensitiveConfigKeys { get; set; } =
    [
        "Mra.Jwt",
        "Mra.TerminalSecret",
        "Terminal.SecretKey"
    ];
}
