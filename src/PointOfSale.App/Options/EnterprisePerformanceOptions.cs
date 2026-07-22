namespace PointOfSale.App.Options;

/// <summary>Post-deployment performance profiling and corporate telemetry flush.</summary>
public sealed class EnterprisePerformanceOptions
{
    public const string SectionName = "EnterprisePerformance";

    public bool Enabled { get; set; } = true;

    /// <summary>Local sampling interval in seconds.</summary>
    public int ProfilingIntervalSeconds { get; set; } = 30;

    /// <summary>Corporate fleet telemetry ingest URL (HTTPS).</summary>
    public string CorporateTelemetryEndpoint { get; set; } = string.Empty;

    /// <summary>Optional fleet status query URL (GET).</summary>
    public string FleetStatusEndpoint { get; set; } = string.Empty;

    /// <summary>Authorization header value for corporate endpoints.</summary>
    public string CorporateAuthorizationHeader { get; set; } = string.Empty;

    public int TelemetryFlushIntervalSeconds { get; set; } = 300;

    public int HttpTimeoutSeconds { get; set; } = 20;

    /// <summary>Window for UI FPS calculation in seconds.</summary>
    public int UiFpsWindowSeconds { get; set; } = 2;
}

public sealed class EnterpriseMaintenanceOptions
{
    public const string SectionName = "EnterpriseMaintenance";

    public bool AllowMaintenanceDuringOpenShift { get; set; }

    public int IndexReorganizeCommandTimeoutSeconds { get; set; } = 120;

    public bool RequireSupervisorForIndexMaintenance { get; set; } = true;
}
