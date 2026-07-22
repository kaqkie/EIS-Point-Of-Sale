namespace PointOfSale.App.Options;

/// <summary>System health monitoring, telemetry sinks, and diagnostic retention.</summary>
public sealed class SystemDiagnosticsOptions
{
    public const string SectionName = "SystemDiagnostics";

    public bool Enabled { get; set; } = true;

    /// <summary>Health check interval in seconds.</summary>
    public int HealthCheckIntervalSeconds { get; set; } = 45;

    /// <summary>Warn when free disk space on the app volume falls below this many MB.</summary>
    public long MinimumFreeDiskMegabytes { get; set; } = 500;

    /// <summary>Warn when SQL round-trip exceeds this many milliseconds.</summary>
    public int DatabaseLatencyWarnMs { get; set; } = 1500;

    /// <summary>Warn when pending+syncing offline invoices exceed this count.</summary>
    public int QueueBacklogWarnCount { get; set; } = 25;

    /// <summary>Minimum seconds between identical health alert codes.</summary>
    public int HealthAlertCooldownSeconds { get; set; } = 120;

    /// <summary>Days to retain DiagnosticTelemetryEvents rows.</summary>
    public int TelemetryRetentionDays { get; set; } = 21;

    /// <summary>Max diagnostic rows returned to the dashboard.</summary>
    public int DashboardLogTake { get; set; } = 200;

    /// <summary>Relative folder under the app base directory for rotated diagnostic files.</summary>
    public string DiagnosticLogDirectory { get; set; } = "Logs/Diagnostics";

    public int DiagnosticFileRetainedDays { get; set; } = 14;
}

public static class DiagnosticEventCategories
{
    public const string Exception = "Exception";
    public const string DatabaseLatency = "DatabaseLatency";
    public const string WorkerHeartbeat = "WorkerHeartbeat";
    public const string MraConnectivity = "MraConnectivity";
    public const string HealthCheck = "HealthCheck";
    public const string Disk = "Disk";
    public const string Printer = "Printer";
    public const string Performance = "Performance";
    public const string Maintenance = "Maintenance";
}

public static class DiagnosticSeverities
{
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";
    public const string Critical = "Critical";
}
