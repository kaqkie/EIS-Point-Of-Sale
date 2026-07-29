namespace PointOfSale.Core.Constants;

public static class DeploymentConfigurationKeys
{
    public const string HardwareFingerprintSha256 = "deployment.hardware.fingerprintSha256";
    public const string TaxpayerTin = "deployment.taxpayer.tin";
    public const string SiteIdOverride = "deployment.siteId";
    public const string ProvisionedAtUtc = "deployment.provisionedAtUtc";
    public const string PackagingChannel = "deployment.packagingChannel";
    public const string TerminalDisplayName = "deployment.terminal.displayName";
    public const string BranchId = "deployment.branchId";
    public const string MerchantAddress = "deployment.merchant.address";
    public const string MerchantPhone = "deployment.merchant.phone";
    public const string MerchantEmail = "deployment.merchant.email";
    public const string FirstRunCompleted = "FirstRun.Completed";
    public const string MraEnvironmentPreference = "FirstRun.MraEnvironment";
}
