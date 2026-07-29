using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.Mra.Contracts.Sales;

/// <summary>
/// Typed EIS envelope for <c>POST sales/last-submitted-offline-transaction</c>
/// (<c>statusCode</c>, <c>remark</c>, <c>errors</c>, nested <c>data</c>).
/// </summary>
public sealed class LastSubmittedOfflineTransactionResponse : EisApiResponse<SubmittedTransactionData>
{
}

/// <summary>
/// Convenience helpers over <see cref="SubmittedTransactionData"/> for Albert Retail Terminal
/// offline-to-online reconciliation.
/// </summary>
public static class SubmittedTransactionDataExtensions
{
    public static string? ResolveInvoiceNumber(this SubmittedTransactionData? data) =>
        data?.InvoiceHeader?.InvoiceNumber?.Trim();

    public static bool HasCompositeInvoiceNumber(this SubmittedTransactionData? data) =>
        Billing.MraInvoiceNumberGenerator.IsMraCompositeInvoiceNumber(ResolveInvoiceNumber(data));

    public static bool TryGetSequenceParts(
        this SubmittedTransactionData? data,
        out (long TaxpayerId, int TerminalPosition, int JulianDate, long TransactionCount) parts) =>
        Billing.MraInvoiceNumberGenerator.TryParseComposite(ResolveInvoiceNumber(data), out parts);
}
