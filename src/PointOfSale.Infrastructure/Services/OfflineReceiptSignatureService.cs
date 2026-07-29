using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Mra.Billing;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Options;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Signs offline sales for Albert Retail Terminal using the MRA ValidationURL / HMAC-SHA256 algorithm
/// and the terminal secret key from onboarding.
/// </summary>
public sealed class OfflineReceiptSignatureService
{
    private readonly IMraTerminalAuthProvider _authProvider;
    private readonly MraApiOptions _options;
    private readonly ILogger<OfflineReceiptSignatureService> _logger;

    public OfflineReceiptSignatureService(
        IMraTerminalAuthProvider authProvider,
        IOptions<MraApiOptions> options,
        ILogger<OfflineReceiptSignatureService> logger)
    {
        _authProvider = authProvider;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Builds <c>offlineDataSignature</c> + full offline <c>ValidationURL</c> for the sales request.
    /// </summary>
    public async Task<MraOfflineReceiptSignatureResult> SignAsync(
        SubmitSalesTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(context.SecretKey))
        {
            throw new InvalidOperationException(
                "Terminal secret key is unavailable. Complete onboarding before signing offline receipts.");
        }

        var baseUrl = _options.ResolveOfflineReceiptValidationBaseUrl();
        var result = MraOfflineReceiptSigning.GenerateFromSalesRequest(
            request,
            context.SecretKey,
            baseUrl);

        _logger.LogInformation(
            "Generated offline ValidationURL signature for invoice {InvoiceNumber} (N={NumItems}, julian={Julian}).",
            result.InvoiceNumber,
            result.NumItems,
            result.JulianDate);

        return result;
    }

    /// <summary>
    /// Applies <c>invoiceSummary.offlineSignature</c> from the MRA offline HMAC-SHA256 algorithm
    /// for local queue persistence and later EIS submit.
    /// </summary>
    public async Task<SubmitSalesTransactionRequest> AttachOfflineSignatureAsync(
        SubmitSalesTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        var signed = await SignAsync(request, cancellationToken).ConfigureAwait(false);
        return request with
        {
            InvoiceSummary = request.InvoiceSummary with
            {
                OfflineSignature = signed.OfflineDataSignature
            }
        };
    }
}
