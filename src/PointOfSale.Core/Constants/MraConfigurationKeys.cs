namespace PointOfSale.Core.Constants;

public static class MraConfigurationKeys
{
    public const string GlobalConfiguration = "mra.configuration.global";
    public const string TerminalConfiguration = "mra.configuration.terminal";
    public const string TaxpayerConfiguration = "mra.configuration.taxpayer";
    public const string JwtToken = "mra.auth.jwt";
    public const string TerminalActivationCode = "mra.onboarding.terminalActivationCode";
    public const string PendingSecretKey = "mra.onboarding.pendingSecretKey";
    public const string ActiveTerminalId = "pos.terminal.activeId";
}

public static class TerminalActivationStates
{
    public const string NotActivated = "NotActivated";
    public const string PendingConfirmation = "PendingConfirmation";
    public const string Activated = "Activated";
    public const string Deactivated = "Deactivated";
}

public static class OfflineQueueStatuses
{
    public const string Pending = "PENDING";
    public const string Syncing = "SYNCING";
    public const string Synced = "SYNCED";
    public const string Quarantined = "QUARANTINED";
}
