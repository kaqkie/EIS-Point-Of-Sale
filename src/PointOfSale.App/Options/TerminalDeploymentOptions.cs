namespace PointOfSale.App.Options;

/// <summary>
/// Live counter deployment settings — branch/site identity and activation placeholders.
/// Sensitive values must not be committed; production secrets are DPAPI-encrypted in SQL after onboarding.
/// </summary>
public sealed class TerminalDeploymentOptions
{
    public const string SectionName = "TerminalDeployment";

    /// <summary>Branch / outlet identifier for operator display and support tickets.</summary>
    public string BranchId { get; set; } = string.Empty;

    /// <summary>MRA site id override when not yet present in terminal configuration cache.</summary>
    public string SiteId { get; set; } = string.Empty;

    /// <summary>
    /// Registered MRA taxpayer TIN (Production) or sandbox developer TIN (trial).
    /// Sandbox/trial may use <c>1234567890</c>; Production must use the live registered TIN.
    /// </summary>
    public string TaxpayerTin { get; set; } = string.Empty;

    /// <summary>
    /// One-time terminal activation code (TAC) supplied at deploy time.
    /// Cleared from config after successful activation; never store long-term in plain text.
    /// </summary>
    public string TerminalActivationCode { get; set; } = string.Empty;

    /// <summary>When true (Production), refuse to start fiscal operations without DPAPI-protected JWT/secret.</summary>
    public bool RequireEncryptedSecrets { get; set; } = true;

    /// <summary>Operator-facing trading name printed on receipts when MRA terminal config is unavailable.</summary>
    public string FallbackTradingName { get; set; } = "Albert Retail Terminal";
}
