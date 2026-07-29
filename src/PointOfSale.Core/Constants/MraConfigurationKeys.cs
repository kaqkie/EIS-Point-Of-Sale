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
    public const string TerminalPosition = "mra.terminal.position";
    public const string DailyInvoiceSequencePrefix = "mra.sales.invoiceSequence.";
    public const string StockHsCodesCache = "mra.stock.hscodes.cache";
    public const string StockUnitsOfMeasureCache = "mra.stock.uom.cache";
    public const string TerminalSiteProductsCachePrefix = "mra.utilities.terminalSiteProducts.";
    public const string Vat5CertificateBalancePrefix = "mra.utilities.vat5.balance.";
    public const string TerminalBlockingState = "mra.utilities.terminalBlocking.state";
    public const string InitialInventoryUploadState = "mra.utilities.initialInventoryUpload.state";
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
