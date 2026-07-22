namespace PointOfSale.App.Options;

/// <summary>Fiscal year boundaries, rollover gates, and long-term archival compression.</summary>
public sealed class FiscalArchivalOptions
{
    public const string SectionName = "FiscalArchival";

    /// <summary>Malawi-style tax year commonly starts 1 July (month 7).</summary>
    public int FiscalYearStartMonth { get; set; } = 7;

    public int FiscalYearStartDay { get; set; } = 1;

    /// <summary>Compress/export operational data older than this many months.</summary>
    public int StaleDataAgeMonths { get; set; } = 12;

    /// <summary>Relative or absolute folder for encrypted fiscal packages.</summary>
    public string ArchiveDirectory { get; set; } = string.Empty;

    /// <summary>When true, rollover requires every business day in the fiscal year to have an EOD closure.</summary>
    public bool RequireAllDailyClosures { get; set; } = true;

    /// <summary>Allow rollover when some days lack closure (manager workflow only).</summary>
    public bool AllowRolloverWithGaps { get; set; }

    /// <summary>After successful archive, purge synced invoices older than stale threshold from active DB.</summary>
    public bool PurgeArchivedSalesFromActiveDatabase { get; set; }

    /// <summary>After successful archive, purge diagnostic telemetry older than stale threshold.</summary>
    public bool PurgeArchivedTelemetryFromActiveDatabase { get; set; }

    public bool EnableBackgroundArchiving { get; set; }

    /// <summary>Background scan interval in hours.</summary>
    public int BackgroundScanIntervalHours { get; set; } = 168;

    /// <summary>Terminal HMAC secret config key fallback for manifest sealing (Configurations).</summary>
    public string TerminalHmacConfigKey { get; set; } = "Mra.TerminalSecret";
}
