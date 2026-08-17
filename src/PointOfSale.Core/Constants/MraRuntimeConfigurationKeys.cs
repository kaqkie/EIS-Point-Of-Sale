namespace PointOfSale.Core.Constants;

public static class MraRuntimeConfigurationKeys
{
    public const string RuntimeEnvironment = "mra.eis.runtimeEnvironment";
    public const string LastHandshakeUtc = "mra.eis.lastHandshakeUtc";
    public const string LastSuccessfulSyncUtc = "mra.eis.lastSuccessfulSyncUtc";
    /// <summary>
    /// Last time this terminal successfully reached MRA (ping/config sync).
    /// Used to enforce OfflineLimit.maxTransactionAgeInHours as a wall-clock offline window.
    /// </summary>
    public const string LastMraReachableUtc = "mra.eis.lastMraReachableUtc";
    public const string CertificateNotAfterUtc = "mra.eis.certificateNotAfterUtc";
    public const string FiscalLockoutActive = "mra.eis.fiscalLockoutActive";
}
