namespace PointOfSale.Core.Entities;

public sealed class DiagnosticTelemetryEvent
{
    public long EventId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? DetailJson { get; set; }
    public int? LatencyMs { get; set; }
    public string? HttpStatus { get; set; }
}

public sealed class SystemHealthSnapshot
{
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsDatabaseHealthy { get; set; }
    public int DatabaseLatencyMs { get; set; }
    public bool IsDiskHealthy { get; set; }
    public long AvailableDiskSpaceBytes { get; set; }
    public string DiskRoot { get; set; } = string.Empty;
    public bool IsPrinterHealthy { get; set; }
    public string PrinterStatus { get; set; } = string.Empty;
    public bool IsMraHealthy { get; set; }
    public string MraApiStatus { get; set; } = string.Empty;
    public int? MraPingMs { get; set; }
    public bool OverallHealthy => IsDatabaseHealthy && IsDiskHealthy && IsPrinterHealthy && IsMraHealthy;
    public string Summary { get; set; } = string.Empty;
}
