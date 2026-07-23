using System.Windows;
using Microsoft.Extensions.Logging;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.App.Services;

public sealed class OfflineInvoiceSyncReceiptHandler : IOfflineInvoiceSyncCompletedHandler
{
    private readonly IReceiptPrintingService _receiptPrintingService;
    private readonly IPosConfigurationService _posConfigurationService;
    private readonly ILogger<OfflineInvoiceSyncReceiptHandler> _logger;

    public OfflineInvoiceSyncReceiptHandler(
        IReceiptPrintingService receiptPrintingService,
        IPosConfigurationService posConfigurationService,
        ILogger<OfflineInvoiceSyncReceiptHandler> logger)
    {
        _receiptPrintingService = receiptPrintingService;
        _posConfigurationService = posConfigurationService;
        _logger = logger;
    }

    public async Task HandleSuccessfulSyncAsync(
        SubmitSalesTransactionRequest payload,
        SubmitSalesTransactionResponseData fiscalResponse,
        CancellationToken cancellationToken = default)
    {
        if (!QueueReceiptPrintHelper.HasPrintableFiscalData(fiscalResponse))
        {
            _logger.LogWarning(
                "Skipping auto-print for invoice {InvoiceNumber}: missing fiscal signature and verification URL.",
                payload.InvoiceHeader.InvoiceNumber);
            return;
        }

        var context = await _posConfigurationService.GetRuntimeContextAsync(cancellationToken).ConfigureAwait(false);
        var printRequest = QueueReceiptPrintHelper.CreatePrintRequest(context, payload, fiscalResponse);

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _receiptPrintingService.PrintAsync(printRequest, CancellationToken.None).GetAwaiter().GetResult();
                _logger.LogInformation(
                    "Auto-printed receipt for synced invoice {InvoiceNumber}.",
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
    public static bool HasPrintableFiscalData(SubmitSalesTransactionResponseData? response) =>
        response is not null &&
        (!string.IsNullOrWhiteSpace(response.VerificationUrl) ||
         !string.IsNullOrWhiteSpace(response.ResolveFiscalSignature()));

    public static ReceiptPrintRequest CreatePrintRequest(
        PosRuntimeContext context,
        SubmitSalesTransactionRequest payload,
        SubmitSalesTransactionResponseData? fiscalResponse) =>
        new()
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
            FiscalResponse = fiscalResponse
        };
}
