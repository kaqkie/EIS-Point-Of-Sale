using PointOfSale.Mra.Billing;
using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.Mra.Services;

/// <summary>
/// Shared composite invoice-number continuity checks for last-submitted online/offline EIS payloads.
/// </summary>
internal static class LastSubmittedInvoiceSequenceValidator
{
    public static InvoiceSequenceCheck Check(
        SubmittedTransactionData data,
        string? expectedSellerTin = null,
        int? expectedTerminalPosition = null,
        long? localDailySequenceFloor = null,
        IReadOnlyCollection<string>? pendingLocalInvoiceNumbers = null,
        long? expectedFiscalTaxpayerId = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        var invoiceNumber = data.ResolveInvoiceNumber();
        if (!MraInvoiceNumberGenerator.TryParseComposite(invoiceNumber, out var remote))
        {
            return InvoiceSequenceCheck.Unparseable(
                invoiceNumber,
                "invoiceNumber is not a valid MRA composite (TIN-Terminal-Julian-Count).");
        }

        var expectedEncodedId = expectedFiscalTaxpayerId is > 0
            ? expectedFiscalTaxpayerId.Value
            : MraInvoiceNumberGenerator.TryParseTaxpayerId(expectedSellerTin, out var tinDigits)
                ? tinDigits
                : 0L;

        if (expectedEncodedId > 0 && remote.TaxpayerId != expectedEncodedId)
        {
            return InvoiceSequenceCheck.Mismatch(
                invoiceNumber!,
                remote,
                $"Encoded taxpayer id {remote.TaxpayerId} does not match expected fiscal id {expectedEncodedId}.");
        }

        if (expectedTerminalPosition is int terminal && terminal > 0 && remote.TerminalPosition != terminal)
        {
            return InvoiceSequenceCheck.Mismatch(
                invoiceNumber!,
                remote,
                $"Encoded terminal position {remote.TerminalPosition} does not match expected position {terminal}.");
        }

        if (localDailySequenceFloor is long floor && floor >= 0 && remote.TransactionCount < floor)
        {
            return InvoiceSequenceCheck.Warning(
                invoiceNumber!,
                remote,
                $"Remote transaction count {remote.TransactionCount} is behind local floor {floor}.");
        }

        if (pendingLocalInvoiceNumbers is { Count: > 0 })
        {
            foreach (var pending in pendingLocalInvoiceNumbers)
            {
                if (!MraInvoiceNumberGenerator.TryParseComposite(pending, out var local))
                {
                    continue;
                }

                if (local.TaxpayerId == remote.TaxpayerId
                    && local.TerminalPosition == remote.TerminalPosition
                    && local.JulianDate == remote.JulianDate
                    && local.TransactionCount <= remote.TransactionCount)
                {
                    return InvoiceSequenceCheck.Mismatch(
                        invoiceNumber!,
                        remote,
                        $"Pending local invoice {pending} does not continue after remote count {remote.TransactionCount}.");
                }
            }
        }

        return InvoiceSequenceCheck.Ok(invoiceNumber!, remote);
    }
}

/// <summary>
/// Result of validating an MRA composite <c>invoiceNumber</c> against expected terminal identity / sequence.
/// </summary>
public sealed class InvoiceSequenceCheck
{
    public bool IsValid { get; init; }
    public bool IsWarning { get; init; }
    public string? InvoiceNumber { get; init; }
    public string? Message { get; init; }
    public long? TaxpayerId { get; init; }
    public int? TerminalPosition { get; init; }
    public int? JulianDate { get; init; }
    public long? TransactionCount { get; init; }

    public static InvoiceSequenceCheck Ok(
        string invoiceNumber,
        (long TaxpayerId, int TerminalPosition, int JulianDate, long TransactionCount) parts) =>
        new()
        {
            IsValid = true,
            InvoiceNumber = invoiceNumber,
            Message = "Invoice number matches expected MRA sequence structure.",
            TaxpayerId = parts.TaxpayerId,
            TerminalPosition = parts.TerminalPosition,
            JulianDate = parts.JulianDate,
            TransactionCount = parts.TransactionCount
        };

    public static InvoiceSequenceCheck Warning(
        string invoiceNumber,
        (long TaxpayerId, int TerminalPosition, int JulianDate, long TransactionCount) parts,
        string message) =>
        new()
        {
            IsValid = true,
            IsWarning = true,
            InvoiceNumber = invoiceNumber,
            Message = message,
            TaxpayerId = parts.TaxpayerId,
            TerminalPosition = parts.TerminalPosition,
            JulianDate = parts.JulianDate,
            TransactionCount = parts.TransactionCount
        };

    public static InvoiceSequenceCheck Mismatch(
        string invoiceNumber,
        (long TaxpayerId, int TerminalPosition, int JulianDate, long TransactionCount) parts,
        string message) =>
        new()
        {
            IsValid = false,
            InvoiceNumber = invoiceNumber,
            Message = message,
            TaxpayerId = parts.TaxpayerId,
            TerminalPosition = parts.TerminalPosition,
            JulianDate = parts.JulianDate,
            TransactionCount = parts.TransactionCount
        };

    public static InvoiceSequenceCheck Unparseable(string? invoiceNumber, string message) =>
        new()
        {
            IsValid = false,
            InvoiceNumber = invoiceNumber,
            Message = message
        };
}
