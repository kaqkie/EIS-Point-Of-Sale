using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.ViewModels;

public partial class QueueSyncStatusViewModel : ObservableObject
{
    private readonly IOfflineInvoiceQueueRepository _queueRepository;
    private readonly OfflineSalesQueueService _offlineSalesQueueService;
    private readonly IReceiptPrintingService _receiptPrintingService;
    private readonly IPosConfigurationService _posConfigurationService;
    private readonly Timer _refreshTimer;

    public QueueSyncStatusViewModel(
        IOfflineInvoiceQueueRepository queueRepository,
        OfflineSalesQueueService offlineSalesQueueService,
        IReceiptPrintingService receiptPrintingService,
        IPosConfigurationService posConfigurationService)
    {
        _queueRepository = queueRepository;
        _offlineSalesQueueService = offlineSalesQueueService;
        _receiptPrintingService = receiptPrintingService;
        _posConfigurationService = posConfigurationService;
        QueueItems = new ObservableCollection<QueueItemViewModel>();
        _refreshTimer = new Timer(async _ => await RefreshAsync().ConfigureAwait(false), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        _ = RefreshAsync();
    }

    public ObservableCollection<QueueItemViewModel> QueueItems { get; }

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _syncingCount;

    [ObservableProperty]
    private int _syncedCount;

    [ObservableProperty]
    private int _quarantinedCount;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var counts = await _queueRepository.GetStatusCountsAsync().ConfigureAwait(true);
        PendingCount = counts.GetValueOrDefault(OfflineQueueStatuses.Pending);
        SyncingCount = counts.GetValueOrDefault(OfflineQueueStatuses.Syncing);
        SyncedCount = counts.GetValueOrDefault(OfflineQueueStatuses.Synced);
        QuarantinedCount = counts.GetValueOrDefault(OfflineQueueStatuses.Quarantined);

        QueueItems.Clear();
        var items = await _queueRepository.GetRecentItemsAsync(100).ConfigureAwait(true);
        foreach (var item in items)
        {
            QueueItems.Add(QueueItemViewModel.FromEntity(item));
        }
    }

    [RelayCommand]
    private async Task SyncNextAsync()
    {
        var processed = await _offlineSalesQueueService.ProcessNextFifoAsync().ConfigureAwait(true);
        StatusMessage = processed ? "Processed next FIFO queue item." : "No eligible queue item.";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RetryQuarantinedAsync(QueueItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var ok = await _queueRepository.RetryQuarantinedAsync(item.Id).ConfigureAwait(true);
        StatusMessage = ok
            ? $"Queue item {item.Id} returned to PENDING."
            : $"Unable to retry item {item.Id}.";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PrintSyncedReceiptAsync(QueueItemViewModel? item)
    {
        if (item is null || !item.Status.Equals(OfflineQueueStatuses.Synced, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var payload = JsonSerializer.Deserialize<SubmitSalesTransactionRequest>(item.PayloadJson, MraJson.SerializerOptions);
        if (payload is null)
        {
            return;
        }

        var context = await _posConfigurationService.GetRuntimeContextAsync().ConfigureAwait(true);
        await _receiptPrintingService.PrintAsync(
            new ReceiptPrintRequest
            {
                TradingName = context.TradingName,
                SellerTin = context.SellerTin,
                AddressLines = context.AddressLines,
                InvoiceNumber = payload.InvoiceHeader.InvoiceNumber,
                InvoiceDateTime = payload.InvoiceHeader.InvoiceDateTime,
                LineItems = payload.InvoiceLineItems,
                TaxBreakdown = payload.InvoiceSummary.TaxBreakDown,
                InvoiceTotal = payload.InvoiceSummary.InvoiceTotal,
                AmountTendered = payload.InvoiceSummary.AmountTendered,
                FiscalResponse = new SubmitSalesTransactionResponseData
                {
                    InvoiceNumber = payload.InvoiceHeader.InvoiceNumber,
                    FiscalSignature = payload.InvoiceSummary.OfflineSignature
                }
            }).ConfigureAwait(true);
    }
}

public sealed class QueueItemViewModel
{
    public int Id { get; init; }
    public required string Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public int RetryCount { get; init; }
    public DateTime? NextRetryTime { get; init; }
    public string? ErrorMessage { get; init; }
    public required string PayloadJson { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;

    public static QueueItemViewModel FromEntity(PointOfSale.Core.Entities.OfflineInvoiceQueueItem entity)
    {
        var invoiceNumber = string.Empty;
        try
        {
            var payload = JsonSerializer.Deserialize<SubmitSalesTransactionRequest>(entity.PayloadJson, MraJson.SerializerOptions);
            invoiceNumber = payload?.InvoiceHeader.InvoiceNumber ?? string.Empty;
        }
        catch
        {
            // ignore parse errors in diagnostic grid
        }

        return new QueueItemViewModel
        {
            Id = entity.Id,
            Status = entity.Status,
            CreatedAt = entity.CreatedAt,
            RetryCount = entity.RetryCount,
            NextRetryTime = entity.NextRetryTime,
            ErrorMessage = entity.ErrorMessage,
            PayloadJson = entity.PayloadJson,
            InvoiceNumber = invoiceNumber
        };
    }
}
