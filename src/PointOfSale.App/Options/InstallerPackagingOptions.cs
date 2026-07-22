namespace PointOfSale.App.Options;

/// <summary>ClickOnce / MSIX packaging and first-run directory provisioning.</summary>
public sealed class InstallerPackagingOptions
{
    public const string SectionName = "InstallerPackaging";

    public string ProductDisplayName { get; set; } = "Albert Retail Terminal";

    public string PublisherDisplayName { get; set; } = "Albert Retail";

    public string MsixPackageFamilyName { get; set; } = "AlbertRetail.AlbertRetailTerminal";

    public string ClickOnceInstallUrl { get; set; } = "https://updates.albertretail.local/art/clickonce/";

    public string MsixInstallUrl { get; set; } = "https://updates.albertretail.local/art/msix/";

    /// <summary>Relative folders created under the application base on first launch.</summary>
    public string[] RelativeDataDirectories { get; set; } =
    [
        "Logs",
        "Logs/MraAudit",
        "Logs/Diagnostics",
        "Backups",
        "Archives/Fiscal"
    ];

    public bool EnforceHardwareBinding { get; set; } = true;

    public bool AttemptSqlExpressDetection { get; set; } = true;

    public string SqlExpressInstanceName { get; set; } = "SQLEXPRESS";

    public string SqlExpressSetupMediaPath { get; set; } = string.Empty;
}
