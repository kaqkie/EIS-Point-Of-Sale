using PointOfSale.Mra.Contracts.Common;

namespace PointOfSale.Mra.Contracts.Sales;

/// <summary>
/// Typed EIS envelope for <c>POST sales/last-submitted-online-transaction</c>
/// (<c>statusCode</c>, <c>remark</c>, <c>errors</c>, nested <c>data</c> with <c>dateSubmitted</c>).
/// Invoice detail maps through <see cref="SubmittedTransactionData"/> —
/// <c>invoiceHeader</c>, <c>invoiceLineItems</c>, and <c>invoiceSummary</c>.
/// </summary>
public sealed class LastSubmittedOnlineTransactionResponse : EisApiResponse<SubmittedTransactionData>
{
}
