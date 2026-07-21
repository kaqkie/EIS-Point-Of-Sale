namespace PointOfSale.App.Options;

public sealed class ApplicationUpdateOptions
{
    public const string SectionName = "ApplicationUpdate";

    /// <summary>When false, update checks are disabled (typical for locked-down pilot terminals).</summary>
    public bool Enabled { get; set; }

    /// <summary>HTTPS URL returning an update manifest JSON (see Setup/update-feed.example.json).</summary>
    public string FeedUrl { get; set; } = string.Empty;

    /// <summary>How often to poll the feed while the app is running.</summary>
    public int CheckIntervalMinutes { get; set; } = 60;

    /// <summary>Download packages in the background; apply only on next restart (zero cashier downtime).</summary>
    public bool StageOnlyDuringBusinessHours { get; set; } = true;

    /// <summary>Optional HMAC or bearer token header value for the internal feed.</summary>
    public string FeedAuthorizationHeader { get; set; } = string.Empty;

    public int HttpTimeoutSeconds { get; set; } = 30;
}

public sealed class DatabaseBootstrapOptions
{
    public const string SectionName = "DatabaseBootstrap";

    public bool Enabled { get; set; } = true;

    /// <summary>Target schema version applied by the in-app bootstrapper.</summary>
    public int TargetSchemaVersion { get; set; } = 15;

    /// <summary>SQL Express instance host from connection string; used for reachability checks.</summary>
    public string RequiredInstanceHint { get; set; } = ".\\SQLEXPRESS";
}
