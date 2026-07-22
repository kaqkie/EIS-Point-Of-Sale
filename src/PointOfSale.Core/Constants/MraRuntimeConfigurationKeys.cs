namespace PointOfSale.Core.Constants;

public static class MraRuntimeConfigurationKeys
{
    public const string RuntimeEnvironment = "mra.eis.runtimeEnvironment";
    public const string LastHandshakeUtc = "mra.eis.lastHandshakeUtc";
    public const string LastSuccessfulSyncUtc = "mra.eis.lastSuccessfulSyncUtc";
    public const string CertificateNotAfterUtc = "mra.eis.certificateNotAfterUtc";
    public const string FiscalLockoutActive = "mra.eis.fiscalLockoutActive";
}
