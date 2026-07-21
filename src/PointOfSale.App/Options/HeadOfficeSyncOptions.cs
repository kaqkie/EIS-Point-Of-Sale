namespace PointOfSale.App.Options;

/// <summary>
/// Branch ↔ head-office / cloud replication settings. Local POS remains offline-first;
/// sync only runs when the network is available and Enabled is true.
/// </summary>
public sealed class HeadOfficeSyncOptions
{
    public const string SectionName = "HeadOfficeSync";

    /// <summary>When false, packaging still works locally but HTTP push/pull is skipped.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the central head-office API (e.g. https://hq.albertretail.local/).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Relative path for uploading encrypted branch deltas.</summary>
    public string UploadPath { get; set; } = "api/v1/branch/sync/upload";

    /// <summary>Relative path for pulling master catalog deltas.</summary>
    public string CatalogDeltaPath { get; set; } = "api/v1/catalog/delta";

    /// <summary>Optional bearer / API key sent as Authorization header.</summary>
    public string AuthorizationHeader { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded 32-byte AES-256 key used for payload encryption.
    /// Prefer provisioning via secure deploy; leave empty to disable encryption (sandbox only).
    /// </summary>
    public string PayloadEncryptionKeyBase64 { get; set; } = string.Empty;

    /// <summary>Background poll interval while the terminal is online.</summary>
    public int PollIntervalSeconds { get; set; } = 120;

    public int HttpTimeoutSeconds { get; set; } = 45;

    /// <summary>Max outbox rows uploaded per sync cycle.</summary>
    public int MaxBatchSize { get; set; } = 50;

    /// <summary>Branch identifier stamped on every payload (falls back to TerminalDeployment:BranchId).</summary>
    public string BranchId { get; set; } = string.Empty;

    /// <summary>Terminal identifier stamped on every payload.</summary>
    public string TerminalId { get; set; } = string.Empty;
}
