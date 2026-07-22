namespace PointOfSale.Core.Entities;

public sealed class DatabaseMaintenanceDashboard
{
    public long DatabaseSizeMb { get; set; }
    public int FragmentedIndexesCount { get; set; }
    public DateTime? LastOptimizationTimestampUtc { get; set; }
    public bool IsMaintenanceRunning { get; set; }
    public string StatusSummary { get; set; } = string.Empty;
}

public sealed class DatabaseMaintenanceLogEntry
{
    public long LogId { get; set; }
    public DateTime ExecutedAtUtc { get; set; }
    public string Operation { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Detail { get; set; }
    public int DurationMs { get; set; }
}

public static class DatabaseMaintenanceOperations
{
    public const string FullOptimization = "FullOptimization";
    public const string RebuildIndexes = "RebuildIndexes";
    public const string UpdateStatistics = "UpdateStatistics";
    public const string PurgeTelemetry = "PurgeTelemetry";
}
