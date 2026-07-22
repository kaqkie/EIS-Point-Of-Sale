namespace PointOfSale.Core.Entities;

public sealed class PerformanceProfileSnapshot
{
    public DateTime CapturedAtUtc { get; set; } = DateTime.UtcNow;
    public double CpuUsagePercentage { get; set; }
    public long MemoryConsumptionMb { get; set; }
    public int AverageQueryLatencyMs { get; set; }
    public double UiFramesPerSecond { get; set; }
    public int ErrorsLastHour { get; set; }
    public int WarningsLastHour { get; set; }
    public string TerminalId { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
}

public sealed class TerminalFleetStatusEntry
{
    public string TerminalId { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public string Status { get; set; } = "Unknown";
    public double CpuUsagePercentage { get; set; }
    public long MemoryConsumptionMb { get; set; }
    public int AverageQueryLatencyMs { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public bool IsLocalTerminal { get; set; }
}

public static class EnterpriseMaintenanceCommandTypes
{
    public const string ClearCaches = "ClearCaches";
    public const string ReorganizeIndexes = "ReorganizeIndexes";
    public const string RenewMraCredentials = "RenewMraCredentials";
    public const string FlushTelemetry = "FlushTelemetry";
}
