namespace PointOfSale.Core.Entities;

public sealed class TerminalHeartbeatRow
{
    public long HeartbeatId { get; set; }
    public string TerminalId { get; set; } = string.Empty;
    public string BranchId { get; set; } = string.Empty;
    public DateTime LastSeenUtc { get; set; }
    public string Status { get; set; } = "Online";
    public string? HostName { get; set; }
    public int PendingOfflineInvoices { get; set; }
    public decimal OpenShiftExpectedCash { get; set; }
    public string? OpenShiftCashier { get; set; }
}

public sealed class MultiTerminalSyncLedgerItem
{
    public long LedgerId { get; set; }
    public string BranchId { get; set; } = string.Empty;
    public string SourceTerminalId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string EntityKey { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public long SequenceNumber { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? AppliedAtUtc { get; set; }
    public string? AppliedByTerminalId { get; set; }
}

public static class MultiTerminalSyncEventTypes
{
    public const string InventoryDelta = "InventoryDelta";
    public const string ShiftTotals = "ShiftTotals";
    public const string OfflineQueueSnapshot = "OfflineQueueSnapshot";
}
