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
    private readonly SalesTransactionService _salesTransactionService;
    private readonly IReceiptPrintingService _receiptPrintingService;
    private readonly IPosConfigurationService _posConfigurationService;
    private readonly Timer _refreshTimer;

    public QueueSyncStatusViewModel(
        IOfflineInvoiceQueueRepository queueRepository,
        OfflineSalesQueueService offlineSalesQueueService,
        SalesTransactionService salesTransactionService,
        IReceiptPrintingService receiptPrintingService,
        IPosConfigurationService posConfigurationService)
    {
        _queueRepository = queueRepository;
        _offlineSalesQueueService = offlineSalesQueueService;
        _salesTransactionService = salesTransactionService;
        _receiptPrintingService = receiptPrintingService;
        _posConfigurationService = posConfigurationService;
        QueueItems = new ObservableCollection<QueueItemViewModel>();
        StatusFilterOptions = new ObservableCollection<string>
        {
            "All",
            OfflineQueueStatuses.Pending,
            OfflineQueueStatuses.Syncing,
            OfflineQueueStatuses.Quarantined,
            OfflineQueueStatuses.Synced
        };
        SelectedStatusFilter = "All";
        _refreshTimer = new Timer(async _ => await RefreshAsync().ConfigureAwait(false), null, TimeSpan.Zero, TimeSpan.FromSeconds(10));
        _ = RefreshAsync();
    }

    public ObservableCollection<QueueItemViewModel> QueueItems { get; }
    public ObservableCollection<string> StatusFilterOptions { get; }

    [ObservableProperty]
    private QueueItemViewModel? _selectedQueueItem;

    [ObservableProperty]
    private string _selectedStatusFilter;

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

    partial void OnSelectedStatusFilterChanged(string value) => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        var counts = await _queueRepository.GetStatusCountsAsync().ConfigureAwait(true);
        PendingCount = counts.GetValueOrDefault(OfflineQueueStatuses.Pending);
        SyncingCount = counts.GetValueOrDefault(OfflineQueueStatuses.Syncing);
        SyncedCount = counts.GetValueOrDefault(OfflineQueueStatuses.Synced);
        QuarantinedCount = counts.GetValueOrDefault(OfflineQueueStatuses.Quarantined);

        var filter = SelectedStatusFilter.Equals("All", StringComparison.OrdinalIgnoreCase)
            ? null
            : SelectedStatusFilter;

        QueueItems.Clear();
        var items = await _queueRepository.GetItemsAsync(filter, take: 250).ConfigureAwait(true);
        foreach (var item in items.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id))
        {
            QueueItems.Add(QueueItemViewModel.FromEntity(item));
        }
    }

    [RelayCommand]
    private async Task SyncNextAsync()
    {
        var processed = await _offlineSalesQueueService.ProcessNextFifoAsync().ConfigureAwait(true);
        StatusMessage = processed
            ? "Processed next FIFO queue item (auto-print runs when MRA returns fiscal data)."
            : "No eligible queue item.";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RetryQuarantinedAsync(QueueItemViewModel? item)
    {
        item ??= SelectedQueueItem;
        if (item is null || !item.CanRetry)
        {
            StatusMessage = "Select a quarantined item to retry.";
            return;
        }

        var ok = await _queueRepository.RetryQuarantinedAsync(item.Id).ConfigureAwait(true);
        StatusMessage = ok
            ? $"Queue item {item.Id} returned to PENDING."
            : $"Unable to retry item {item.Id}.";
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ForceSyncAsync(QueueItemViewModel? item)
    {
        item ??= SelectedQueueItem;
        if (item is null || !item.CanForceSync)
        {
            StatusMessage = "Select a quarantined or pending item to force sync.";
            return;
        }

        var result = await _offlineSalesQueueService.ForceSyncQueueItemAsync(item.Id).ConfigureAwait(true);
        StatusMessage = result switch
        {
            null => "Queue item not found.",
            { SubmittedOnline: true } => $"Force sync succeeded for invoice {result.InvoiceNumber}. Receipt auto-print triggered when fiscal data is present.",
            { IsQuarantined: true } => $"Force sync quarantined: {result.Remark}",
            _ => result.Remark ?? "Force sync did not complete."
        };
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task PrintReceiptAsync(QueueItemViewModel? item)
    {
        item ??= SelectedQueueItem;
        if (item is null || !item.CanPrintReceipt)
        {
            StatusMessage = "Select a SYNCED item to print its receipt.";
            return;
        }

        var payload = JsonSerializer.Deserialize<SubmitSalesTransactionRequest>(item.PayloadJson, MraJson.SerializerOptions);
        if (payload is null)
        {
            StatusMessage = "Unable to parse invoice payload.";
            return;
        }

        var fiscal = item.ResolveFiscalResponse();
        if (fiscal is null && !string.IsNullOrWhiteSpace(payload.InvoiceHeader.InvoiceNumber))
        {
            var lookup = await _salesTransactionService
                .GetInvoiceByNumberAsync(
                    new InvoiceNumberQueryRequest { InvoiceNumber = payload.InvoiceHeader.InvoiceNumber })
                .ConfigureAwait(true);
            if (lookup.Success && lookup.Data is not null)
            {
                fiscal = new SubmitSalesTransactionResponseData
                {
                    InvoiceNumber = lookup.Data.InvoiceNumber,
                    FiscalSignature = lookup.Data.FiscalCode,
                    VerificationUrl = null
                };
            }
        }

        if (fiscal is null &&
            !string.IsNullOrWhiteSpace(payload.InvoiceSummary.OfflineSignature))
        {
            fiscal = new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = payload.InvoiceHeader.InvoiceNumber,
                FiscalSignature = payload.InvoiceSummary.OfflineSignature
            };
        }

        var context = await _posConfigurationService.GetRuntimeContextAsync().ConfigureAwait(true);
        await _receiptPrintingService.PrintAsync(
            QueueReceiptPrintHelper.CreatePrintRequest(context, payload, fiscal)).ConfigureAwait(true);
        StatusMessage = $"Printed receipt for {payload.InvoiceHeader.InvoiceNumber}.";
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
    public string? FiscalResponseJson { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;

    public bool CanRetry =>
        Status.Equals(OfflineQueueStatuses.Quarantined, StringComparison.OrdinalIgnoreCase);

    public bool CanForceSync =>
        Status.Equals(OfflineQueueStatuses.Quarantined, StringComparison.OrdinalIgnoreCase) ||
        Status.Equals(OfflineQueueStatuses.Pending, StringComparison.OrdinalIgnoreCase);

    public bool CanPrintReceipt =>
        Status.Equals(OfflineQueueStatuses.Synced, StringComparison.OrdinalIgnoreCase);

    public SubmitSalesTransactionResponseData? ResolveFiscalResponse()
    {
        if (string.IsNullOrWhiteSpace(FiscalResponseJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize<SubmitSalesTransactionResponseData>(FiscalResponseJson, MraJson.SerializerOptions);
    }

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
            FiscalResponseJson = entity.FiscalResponseJson,
            InvoiceNumber = invoiceNumber
        };
    }
}
