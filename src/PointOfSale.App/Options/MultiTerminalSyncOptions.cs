namespace PointOfSale.App.Options;

/// <summary>
/// In-store multi-register synchronization over the shared SQL Express database (same branch LAN).
/// Coordinates inventory, shift totals, and offline invoice queue visibility without race conditions.
/// </summary>
public sealed class MultiTerminalSyncOptions
{
    public const string SectionName = "MultiTerminalSync";

    public bool Enabled { get; set; } = true;

    /// <summary>Stable register identity (falls back to HeadOfficeSync:TerminalId / machine name).</summary>
    public string TerminalId { get; set; } = string.Empty;

    /// <summary>Branch / store code stamped on sync ledger rows.</summary>
    public string BranchId { get; set; } = string.Empty;

    /// <summary>Background heartbeat + reconcile interval.</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>Terminals with no heartbeat within this window are marked offline.</summary>
    public int HeartbeatStaleSeconds { get; set; } = 45;

    /// <summary>SQL application-lock timeout when applying inventory deltas (ms).</summary>
    public int InventoryLockTimeoutMs { get; set; } = 5000;

    /// <summary>Max ledger rows applied per synchronize cycle.</summary>
    public int MaxBatchSize { get; set; } = 100;
}
