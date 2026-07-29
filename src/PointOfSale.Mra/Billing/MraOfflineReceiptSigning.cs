using System.Globalization;
using System.Net;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Security;

namespace PointOfSale.Mra.Billing;

/// <summary>
/// Input for MRA offline ValidationURL / <c>offlineDataSignature</c> generation
/// (<c>https://dev-eis-api.mra.mw/docs/signing_offline_receipts.htm</c>).
/// </summary>
public sealed class MraOfflineInvoiceSigningRequest
{
    public required long TaxpayerId { get; init; }
    public required int TerminalPosition { get; init; }
    public required DateTime TransactionDateUtc { get; init; }
    public required long TransactionCount { get; init; }
    public required int NumItems { get; init; }
    public required decimal InvoiceTotal { get; init; }
    public required decimal VatAmount { get; init; }

    /// <summary>
    /// Optional pre-built composite invoice number. When null, one is generated from the identity fields.
    /// </summary>
    public string? InvoiceNumber { get; init; }
}

/// <summary>
/// Result of offline ValidationURL + HMAC-SHA256 signing for Albert Retail Terminal.
/// </summary>
public sealed class MraOfflineReceiptSignatureResult
{
    /// <summary>Composite invoice number used as <c>TI</c>.</summary>
    public required string InvoiceNumber { get; init; }

    /// <summary>Unsigned query string <c>TI=...&amp;N=...&amp;I=...&amp;V=...&amp;T=...</c>.</summary>
    public required string ParameterString { get; init; }

    /// <summary>
    /// Raw Base64URL HMAC-SHA256 of <see cref="ParameterString"/> — persist as
    /// <c>invoiceSummary.offlineSignature</c> / local <c>offlineDataSignature</c>.
    /// </summary>
    public required string OfflineDataSignature { get; init; }

    /// <summary><see cref="OfflineDataSignature"/> after <see cref="WebUtility.UrlEncode"/> for the <c>S</c> query value.</summary>
    public required string UrlEncodedSignature { get; init; }

    /// <summary>Full offline ValidationURL including <c>S</c> (for QR / receipt).</summary>
    public required string ValidationUrl { get; init; }

    public int JulianDate { get; init; }
    public string JulianDateBase64 { get; init; } = string.Empty;
    public int NumItems { get; init; }
    public decimal InvoiceTotal { get; init; }
    public decimal VatAmount { get; init; }
}

/// <summary>
/// MRA offline receipt ValidationURL + HMAC-SHA256 signing helpers.
/// Reuses Julian date / Base64 conversion from <see cref="MraInvoiceNumberGenerator"/>.
/// </summary>
public static class MraOfflineReceiptSigning
{
    public const string DefaultSandboxValidationBaseUrl =
        "https://dev-eis-portal.mra.mw/ReceiptValidation/Validate/";

    public const string DefaultProductionValidationBaseUrl =
        "https://eis-portal.mra.mw/ReceiptValidation/Validate/";

    /// <summary>Converts the transaction date into the MRA Julian day number.</summary>
    public static int ToJulianDate(DateTime transactionDate) =>
        MraInvoiceNumberGenerator.ToJulianDate(transactionDate);

    /// <summary>Encodes a Base10 integer with the MRA compact Base64 alphabet.</summary>
    public static string Base10ToBase64(long number) =>
        MraInvoiceNumberGenerator.Base10ToBase64(number);

    /// <summary>
    /// Builds <c>TI</c>: <c>Base64(TaxpayerID)-Base64(TerminalPosition)-Base64(JulianDate)-Base64(Count)</c>.
    /// </summary>
    public static string GenerateCombinedString(
        long taxpayerId,
        int terminalPosition,
        int julianDate,
        long transactionCount)
    {
        if (taxpayerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(taxpayerId));
        }

        if (terminalPosition <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalPosition));
        }

        if (julianDate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(julianDate));
        }

        if (transactionCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionCount));
        }

        return string.Join(
            '-',
            Base10ToBase64(taxpayerId),
            Base10ToBase64(terminalPosition),
            Base10ToBase64(julianDate),
            Base10ToBase64(transactionCount));
    }

    /// <summary>
    /// Builds the unsigned offline query parameter string
    /// <c>TI={invoice}&amp;N={items}&amp;I={total}&amp;V={vat}&amp;T={julianBase64}</c>.
    /// </summary>
    public static string BuildParameterString(
        string invoiceNumber,
        int numItems,
        decimal invoiceTotal,
        decimal vatAmount,
        string julianDateBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(julianDateBase64);
        if (numItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(numItems));
        }

        // Invariant culture matches the MRA sample string interpolation and avoids locale commas.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"TI={invoiceNumber.Trim()}&N={numItems}&I={invoiceTotal}&V={vatAmount}&T={julianDateBase64.Trim()}");
    }

    /// <summary>
    /// Secure HMAC-SHA256 helper used for offline ValidationURL <c>S</c> / <c>offlineDataSignature</c>.
    /// </summary>
    public static string ComputeHmacWithSha256(string plainText, string secretKey) =>
        HmacSignatureService.ComputeHmacWithSha256(plainText, secretKey);

    /// <summary>
    /// Generates <c>offlineDataSignature</c> and the full offline <c>ValidationURL</c>.
    /// </summary>
    public static MraOfflineReceiptSignatureResult Generate(
        MraOfflineInvoiceSigningRequest request,
        string secretKey,
        string? offlineValidationBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);

        var julianDate = ToJulianDate(request.TransactionDateUtc);
        var julianDateTo64 = Base10ToBase64(julianDate);
        var combinedString = !string.IsNullOrWhiteSpace(request.InvoiceNumber)
            && MraInvoiceNumberGenerator.IsMraCompositeInvoiceNumber(request.InvoiceNumber)
                ? request.InvoiceNumber.Trim()
                : GenerateCombinedString(
                    request.TaxpayerId,
                    request.TerminalPosition,
                    julianDate,
                    request.TransactionCount);

        var param = BuildParameterString(
            combinedString,
            request.NumItems,
            request.InvoiceTotal,
            request.VatAmount,
            julianDateTo64);

        var offlineDataSignature = ComputeHmacWithSha256(param, secretKey);
        var urlEncodedSignature = WebUtility.UrlEncode(offlineDataSignature);
        var baseUrl = NormalizeValidationBaseUrl(
            offlineValidationBaseUrl ?? DefaultSandboxValidationBaseUrl);
        var validationUrl = $"{baseUrl}?{param}&S={urlEncodedSignature}";

        return new MraOfflineReceiptSignatureResult
        {
            InvoiceNumber = combinedString,
            ParameterString = param,
            OfflineDataSignature = offlineDataSignature,
            UrlEncodedSignature = urlEncodedSignature,
            ValidationUrl = validationUrl,
            JulianDate = julianDate,
            JulianDateBase64 = julianDateTo64,
            NumItems = request.NumItems,
            InvoiceTotal = request.InvoiceTotal,
            VatAmount = request.VatAmount
        };
    }

    /// <summary>
    /// Builds a signing request from a sales submit payload (uses composite invoice number segments when available).
    /// </summary>
    public static MraOfflineInvoiceSigningRequest FromSalesRequest(SubmitSalesTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invoiceNumber = request.InvoiceHeader.InvoiceNumber?.Trim() ?? string.Empty;
        long taxpayerId;
        int terminalPosition;
        long transactionCount;
        DateTime transactionDate = request.InvoiceHeader.InvoiceDateTime;

        if (MraInvoiceNumberGenerator.TryParseComposite(invoiceNumber, out var parts))
        {
            taxpayerId = parts.TaxpayerId;
            terminalPosition = parts.TerminalPosition;
            transactionCount = parts.TransactionCount;
        }
        else
        {
            if (!MraInvoiceNumberGenerator.TryParseTaxpayerId(request.InvoiceHeader.SellerTin, out taxpayerId))
            {
                throw new InvalidOperationException(
                    "Cannot sign offline receipt: sellerTIN is not a numeric taxpayer id and invoiceNumber is not MRA composite.");
            }

            terminalPosition = 1;
            transactionCount = 1;
        }

        return new MraOfflineInvoiceSigningRequest
        {
            TaxpayerId = taxpayerId,
            TerminalPosition = terminalPosition,
            TransactionDateUtc = transactionDate,
            TransactionCount = transactionCount,
            NumItems = request.InvoiceLineItems.Count,
            InvoiceTotal = request.InvoiceSummary.InvoiceTotal,
            VatAmount = request.InvoiceSummary.TotalVat,
            InvoiceNumber = string.IsNullOrWhiteSpace(invoiceNumber) ? null : invoiceNumber
        };
    }

    public static MraOfflineReceiptSignatureResult GenerateFromSalesRequest(
        SubmitSalesTransactionRequest request,
        string secretKey,
        string? offlineValidationBaseUrl = null) =>
        Generate(FromSalesRequest(request), secretKey, offlineValidationBaseUrl);

    public static string NormalizeValidationBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var trimmed = baseUrl.Trim();
        return trimmed.EndsWith('/') ? trimmed : trimmed + "/";
    }

    public static string ResolveDefaultValidationBaseUrl(bool isProduction) =>
        isProduction ? DefaultProductionValidationBaseUrl : DefaultSandboxValidationBaseUrl;
}
