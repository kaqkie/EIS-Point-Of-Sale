using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Options;

namespace PointOfSale.App.Services;

/// <summary>
/// Normalizes MRA submit responses so receipts always get a verification URL + fiscal token
/// when the EIS fiscalized the sale (online), without fabricating QR data for offline placeholders.
/// </summary>
public static class FiscalReceiptEnricher
{
    public const string OfflinePendingPlaceholder = "OFFLINE-QUEUED-PENDING-MRA-SYNC";

    public static bool IsOfflinePlaceholder(string? fiscalToken) =>
        !string.IsNullOrWhiteSpace(fiscalToken)
        && (fiscalToken.Contains("OFFLINE-QUEUED", StringComparison.OrdinalIgnoreCase)
            || fiscalToken.Contains("PENDING-MRA-SYNC", StringComparison.OrdinalIgnoreCase));

    public static SubmitSalesTransactionResponseData EnsurePrintableFiscalPayload(
        SubmitSalesTransactionResponseData response,
        string invoiceNumber,
        string? verificationBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);

        var signature = response.ResolveFiscalSignature();
        var verificationUrl = response.ResolveVerificationUrl();

        if (IsOfflinePlaceholder(signature))
        {
            return new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = response.InvoiceNumber ?? invoiceNumber,
                FiscalCode = response.FiscalCode,
                FiscalSignature = string.IsNullOrWhiteSpace(signature) ? OfflinePendingPlaceholder : signature,
                ValidationUrl = null,
                VerificationUrl = null,
                ShouldDownloadLatestConfig = response.ShouldDownloadLatestConfig,
                ShouldBlockTerminal = response.ShouldBlockTerminal,
                ShouldBoardTerminal = response.ShouldBoardTerminal,
                ValidationErrors = response.ValidationErrors
            };
        }

        var baseUrl = ResolveVerificationBase(verificationBaseUrl);

        if (string.IsNullOrWhiteSpace(verificationUrl) && !string.IsNullOrWhiteSpace(signature))
        {
            verificationUrl =
                $"{baseUrl}?invoice={Uri.EscapeDataString(invoiceNumber.Trim())}" +
                $"&sig={Uri.EscapeDataString(signature.Trim())}";
        }
        else if (!string.IsNullOrWhiteSpace(verificationUrl)
                 && !verificationUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                 && !IsOfflinePlaceholder(verificationUrl))
        {
            verificationUrl = $"{baseUrl}/{verificationUrl.TrimStart('/')}";
        }
        else if (!string.IsNullOrWhiteSpace(verificationUrl)
                 && MraApiOptions.IsLegacyUnreachableHost(verificationUrl)
                 && !string.IsNullOrWhiteSpace(signature))
        {
            // Rewrite legacy eis.mra.mw verify links to the reachable portal host.
            verificationUrl =
                $"{baseUrl}?invoice={Uri.EscapeDataString(invoiceNumber.Trim())}" +
                $"&sig={Uri.EscapeDataString(signature.Trim())}";
        }

        return new SubmitSalesTransactionResponseData
        {
            InvoiceNumber = response.InvoiceNumber ?? invoiceNumber,
            FiscalCode = response.FiscalCode,
            FiscalSignature = string.IsNullOrWhiteSpace(response.FiscalSignature) ? signature : response.FiscalSignature,
            ValidationUrl = verificationUrl,
            VerificationUrl = verificationUrl,
            ShouldDownloadLatestConfig = response.ShouldDownloadLatestConfig,
            ShouldBlockTerminal = response.ShouldBlockTerminal,
            ShouldBoardTerminal = response.ShouldBoardTerminal,
            ValidationErrors = response.ValidationErrors
        };
    }

    private static string ResolveVerificationBase(string? verificationBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(verificationBaseUrl)
            && !MraApiOptions.IsLegacyUnreachableHost(verificationBaseUrl))
        {
            return verificationBaseUrl.Trim().TrimEnd('/');
        }

        return MraApiOptions.DefaultSandboxVerificationBaseUrl;
    }
}
