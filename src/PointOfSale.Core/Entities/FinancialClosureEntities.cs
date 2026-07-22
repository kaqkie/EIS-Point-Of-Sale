namespace PointOfSale.Core.Entities;

public sealed class FinancialClosureRecord
{
    public long ClosureId { get; set; }
    public DateTime BusinessDate { get; set; }
    public DateTime ClosedAtUtc { get; set; }
    public string ClosedByUsername { get; set; } = string.Empty;
    public string ClosedByDisplayName { get; set; } = string.Empty;
    public decimal TotalGrossSalesMwk { get; set; }
    public decimal TotalTaxableSalesMwk { get; set; }
    public decimal TotalVatCollectedMwk { get; set; }
    public decimal ExpectedVatMwk { get; set; }
    public decimal VatVarianceMwk { get; set; }
    public decimal CashCollectionsMwk { get; set; }
    public decimal CardSettlementsMwk { get; set; }
    public decimal MobileMoneySettlementsMwk { get; set; }
    public decimal OtherSettlementsMwk { get; set; }
    public decimal TotalVoidsMwk { get; set; }
    public int VoidCount { get; set; }
    public int SyncedInvoiceCount { get; set; }
    public int PendingInvoiceCount { get; set; }
    public int QuarantinedInvoiceCount { get; set; }
    public int FiscalSignatureMatchCount { get; set; }
    public int FiscalSignatureMissingCount { get; set; }
    public decimal CashDrawerVarianceMwk { get; set; }
    public decimal CumulativeGrossSalesMwk { get; set; }
    public decimal CumulativeVatMwk { get; set; }
    public int ShiftCount { get; set; }
    public bool AuditPassed { get; set; }
    public string Status { get; set; } = FinancialClosureStatuses.Closed;
    public string? Notes { get; set; }
    public string? ClosureJson { get; set; }
}

public static class FinancialClosureStatuses
{
    public const string Closed = "Closed";
    public const string Voided = "Voided";
}
