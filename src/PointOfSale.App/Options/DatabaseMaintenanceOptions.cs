namespace PointOfSale.App.Options;

public sealed class DatabaseMaintenanceOptions
{
    public const string SectionName = "DatabaseMaintenance";

    public bool Enabled { get; set; } = true;

    /// <summary>Background maintenance interval in hours.</summary>
    public int MaintenanceIntervalHours { get; set; } = 24;

    /// <summary>Index fragmentation percent threshold before REBUILD.</summary>
    public int RebuildFragmentationPercentThreshold { get; set; } = 30;

    /// <summary>Minimum index page count before maintenance considers an index.</summary>
    public int MinimumIndexPageCount { get; set; } = 64;

    public int CommandTimeoutSeconds { get; set; } = 300;

    public bool AllowRebuildDuringOpenShift { get; set; }

    /// <summary>Diagnostic telemetry retention passed to purge proc.</summary>
    public int TelemetryRetentionDays { get; set; } = 21;

    public int MraAuditLogRetentionDays { get; set; } = 90;
}
