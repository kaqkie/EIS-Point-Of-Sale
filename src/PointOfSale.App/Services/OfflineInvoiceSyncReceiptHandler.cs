using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Options;

namespace PointOfSale.App.Services;

public sealed class OfflineInvoiceSyncReceiptHandler : IOfflineInvoiceSyncCompletedHandler
{
    private readonly IReceiptPrintingService _receiptPrintingService;
    private readonly IPosConfigurationService _posConfigurationService;
    private readonly MraApiOptions _mraOptions;
    private readonly ILogger<OfflineInvoiceSyncReceiptHandler> _logger;

    public OfflineInvoiceSyncReceiptHandler(
        IReceiptPrintingService receiptPrintingService,
        IPosConfigurationService posConfigurationService,
        IOptions<MraApiOptions> mraOptions,
        ILogger<OfflineInvoiceSyncReceiptHandler> logger)
    {
        _receiptPrintingService = receiptPrintingService;
        _posConfigurationService = posConfigurationService;
        _mraOptions = mraOptions.Value;
        _logger = logger;
    }

    public async Task HandleSuccessfulSyncAsync(
        SubmitSalesTransactionRequest payload,
        SubmitSalesTransactionResponseData fiscalResponse,
        CancellationToken cancellationToken = default)
    {
        var verifyBase = _mraOptions.ResolveVerificationBaseUrl();
        var enriched = FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
            fiscalResponse,
            payload.InvoiceHeader.InvoiceNumber,
            verifyBase);

        if (!QueueReceiptPrintHelper.HasPrintableFiscalData(enriched))
        {
            _logger.LogWarning(
                "Skipping auto-print for invoice {InvoiceNumber}: missing fiscal signature and verification URL.",
                payload.InvoiceHeader.InvoiceNumber);
            return;
        }

        var context = await _posConfigurationService.GetRuntimeContextAsync(cancellationToken).ConfigureAwait(false);
        var printRequest = QueueReceiptPrintHelper.CreatePrintRequest(context, payload, enriched, verifyBase);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _receiptPrintingService.PrintAsync(printRequest, CancellationToken.None).GetAwaiter().GetResult();
                _logger.LogInformation(
                    "Auto-printed fiscal receipt with MRA QR for synced invoice {InvoiceNumber}.",
                    payload.InvoiceHeader.InvoiceNumber);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-print failed for invoice {InvoiceNumber}.", payload.InvoiceHeader.InvoiceNumber);
            }
        }).Task.ConfigureAwait(false);
    }
}

public static class QueueReceiptPrintHelper
{
    public static bool HasPrintableFiscalData(SubmitSalesTransactionResponseData? response)
    {
        if (response is null)
        {
            return false;
        }

        var enriched = FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
            response,
            response.InvoiceNumber ?? "UNKNOWN");
        return !string.IsNullOrWhiteSpace(enriched.VerificationUrl)
            || (!string.IsNullOrWhiteSpace(enriched.ResolveFiscalSignature())
                && !FiscalReceiptEnricher.IsOfflinePlaceholder(enriched.ResolveFiscalSignature()));
    }

    public static ReceiptPrintRequest CreatePrintRequest(
        PosRuntimeContext context,
        SubmitSalesTransactionRequest payload,
        SubmitSalesTransactionResponseData? fiscalResponse,
        string? verificationBaseUrl = null)
    {
        var enriched = fiscalResponse is null
            ? null
            : FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
                fiscalResponse,
                payload.InvoiceHeader.InvoiceNumber,
                verificationBaseUrl);

        return new ReceiptPrintRequest
        {
            TradingName = context.TradingName,
            SellerTin = context.SellerTin,
            AddressLines = context.AddressLines,
            InvoiceNumber = payload.InvoiceHeader.InvoiceNumber,
            InvoiceDateTime = payload.InvoiceHeader.InvoiceDateTime,
            LineItems = payload.InvoiceLineItems,
            TaxBreakdown = payload.InvoiceSummary.TaxBreakDown,
            SubtotalNet = payload.InvoiceSummary.InvoiceTotal - payload.InvoiceSummary.TotalVat,
            TotalVat = payload.InvoiceSummary.TotalVat,
            InvoiceTotal = payload.InvoiceSummary.InvoiceTotal,
            AmountTendered = payload.InvoiceSummary.AmountTendered,
            FiscalResponse = enriched
        };
    }
}
