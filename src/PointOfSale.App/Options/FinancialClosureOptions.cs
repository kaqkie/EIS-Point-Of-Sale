namespace PointOfSale.App.Options;

public sealed class FinancialClosureOptions
{
    public const string SectionName = "FinancialClosure";

    /// <summary>When true, EOD close is blocked while PENDING/SYNCING invoices remain.</summary>
    public bool RequireQueueDrained { get; set; } = true;

    /// <summary>When true, every SYNCED invoice must carry an MRA fiscal signature/code.</summary>
    public bool RequireFiscalSignatures { get; set; } = true;

    /// <summary>Allow EOD close while an open cashier shift exists (not recommended).</summary>
    public bool AllowCloseWithOpenShift { get; set; }

    /// <summary>Maximum absolute VAT variance (MWK) accepted as balanced.</summary>
    public decimal VatBalanceToleranceMwk { get; set; } = 0.01m;

    /// <summary>Maximum absolute cash drawer variance (MWK) without forcing manager override notes.</summary>
    public decimal CashVarianceWarnMwk { get; set; } = 50m;
}
