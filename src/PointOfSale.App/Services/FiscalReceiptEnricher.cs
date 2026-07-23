using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.App.Services;

/// <summary>
/// Normalizes MRA submit responses so receipts always get a verification URL + fiscal token
/// when the EIS fiscalized the sale (online), without fabricating QR data for offline placeholders.
/// </summary>
public static class FiscalReceiptEnricher
{
    public const string OfflinePendingPlaceholder = "OFFLINE-QUEUED-PENDING-MRA-SYNC";
    public const string DefaultVerificationBaseUrl = "https://eis.mra.mw/verify";

    public static bool IsOfflinePlaceholder(string? fiscalToken) =>
        string.IsNullOrWhiteSpace(fiscalToken)
        || fiscalToken.Contains("OFFLINE-QUEUED", StringComparison.OrdinalIgnoreCase)
        || fiscalToken.Contains("PENDING-MRA-SYNC", StringComparison.OrdinalIgnoreCase);

    public static SubmitSalesTransactionResponseData EnsurePrintableFiscalPayload(
        SubmitSalesTransactionResponseData response,
        string invoiceNumber,
        string? verificationBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);

        var signature = response.ResolveFiscalSignature();
        var verificationUrl = response.VerificationUrl?.Trim();

        if (IsOfflinePlaceholder(signature))
        {
            return new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = response.InvoiceNumber ?? invoiceNumber,
                FiscalCode = response.FiscalCode,
                FiscalSignature = string.IsNullOrWhiteSpace(signature) ? OfflinePendingPlaceholder : signature,
                VerificationUrl = null,
                ShouldDownloadLatestConfig = response.ShouldDownloadLatestConfig
            };
        }

        if (string.IsNullOrWhiteSpace(verificationUrl) && !string.IsNullOrWhiteSpace(signature))
        {
            var baseUrl = string.IsNullOrWhiteSpace(verificationBaseUrl)
                ? DefaultVerificationBaseUrl
                : verificationBaseUrl.Trim().TrimEnd('/');
            verificationUrl =
                $"{baseUrl}?invoice={Uri.EscapeDataString(invoiceNumber.Trim())}" +
                $"&sig={Uri.EscapeDataString(signature.Trim())}";
        }
        else if (!string.IsNullOrWhiteSpace(verificationUrl)
                 && verificationUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) == false
                 && !IsOfflinePlaceholder(verificationUrl))
        {
            // Some gateways return a relative verify path or bare token — promote to absolute URL.
            var baseUrl = string.IsNullOrWhiteSpace(verificationBaseUrl)
                ? DefaultVerificationBaseUrl
                : verificationBaseUrl.Trim().TrimEnd('/');
            verificationUrl = $"{baseUrl}/{verificationUrl.TrimStart('/')}";
        }

        return new SubmitSalesTransactionResponseData
        {
            InvoiceNumber = response.InvoiceNumber ?? invoiceNumber,
            FiscalCode = response.FiscalCode,
            FiscalSignature = string.IsNullOrWhiteSpace(response.FiscalSignature) ? signature : response.FiscalSignature,
            VerificationUrl = verificationUrl,
            ShouldDownloadLatestConfig = response.ShouldDownloadLatestConfig
        };
    }
}
