namespace PointOfSale.Infrastructure.Options;

public sealed class OfflineSyncOptions
{
    public const string SectionName = "OfflineSync";

    public int PollIntervalSeconds { get; set; } = 15;

    public int MaxRetryAttempts { get; set; } = 8;

    public int BaseBackoffSeconds { get; set; } = 30;

    public int MaxBackoffSeconds { get; set; } = 3600;

    /// <summary>
    /// When true, the FIFO sync worker skips uploads until <see cref="Services.IMraConnectivityMonitor"/>
    /// reports MRA reachable (avoids burning retries while offline).
    /// </summary>
    public bool RequireMraConnectivity { get; set; } = true;

    /// <summary>
    /// Enforce <c>offlineLimit.maxTransactionAgeInHours</c> before upload (quarantine when exceeded).
    /// </summary>
    public bool EnforceTransactionAge { get; set; } = true;

    /// <summary>
    /// Require a non-empty <c>invoiceSummary.offlineSignature</c> on every offline queue upload.
    /// </summary>
    public bool RequireOfflineSignature { get; set; } = true;

    /// <summary>
    /// Fallback max age (hours) when terminal configuration has no offlineLimit.
    /// MRA sandbox seed defaults to 72 hours.
    /// </summary>
    public int DefaultMaxTransactionAgeInHours { get; set; } = 72;

    /// <summary>Maximum items drained in one connectivity-restored burst (0 = unlimited).</summary>
    public int MaxDrainBatchSize { get; set; } = 50;
}
