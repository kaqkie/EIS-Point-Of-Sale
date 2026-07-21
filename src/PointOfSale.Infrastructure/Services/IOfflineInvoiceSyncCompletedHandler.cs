using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Invoked when an offline queue item successfully syncs with MRA (background FIFO or operator force sync).
/// </summary>
public interface IOfflineInvoiceSyncCompletedHandler
{
    Task HandleSuccessfulSyncAsync(
        SubmitSalesTransactionRequest payload,
        SubmitSalesTransactionResponseData fiscalResponse,
        CancellationToken cancellationToken = default);
}
