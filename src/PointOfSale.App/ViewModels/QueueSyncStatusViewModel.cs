using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.ViewModels;

public partial class QueueSyncStatusViewModel : ObservableObject, IDisposable
{
    private readonly IOfflineInvoiceQueueRepository _queueRepository;
    private readonly OfflineSalesQueueService _offlineSalesQueueService;
    private readonly OfflineTransactionSyncService _offlineTransactionSyncService;
    private readonly SalesTransactionService _salesTransactionService;
    private readonly OfflineReceiptSignatureService _offlineReceiptSignatureService;
    private readonly IReceiptPrintingService _receiptPrintingService;
    private readonly IPosConfigurationService _posConfigurationService;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;
    private int _refreshGate;

    public QueueSyncStatusViewModel(
        IOfflineInvoiceQueueRepository queueRepository,
        OfflineSalesQueueService offlineSalesQueueService,
        OfflineTransactionSyncService offlineTransactionSyncService,
        SalesTransactionService salesTransactionService,
        OfflineReceiptSignatureService offlineReceiptSignatureService,
        IReceiptPrintingService receiptPrintingService,
        IPosConfigurationService posConfigurationService)
    {
        _queueRepository = queueRepository;
        _offlineSalesQueueService = offlineSalesQueueService;
        _offlineTransactionSyncService = offlineTransactionSyncService;
        _salesTransactionService = salesTransactionService;
        _offlineReceiptSignatureService = offlineReceiptSignatureService;
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

        // DispatcherTimer keeps collection mutations on the UI thread (avoids CollectionView crashes).
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
        _ = RefreshAsync();
    }

    public ObservableCollection<QueueItemViewModel> QueueItems { get; }
    public ObservableCollection<string> StatusFilterOptions { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintReceiptCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryQuarantinedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceSyncCommand))]
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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintReceiptCommand))]
    [NotifyCanExecuteChangedFor(nameof(RetryQuarantinedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForceSyncCommand))]
    [NotifyCanExecuteChangedFor(nameof(SyncNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(FixReceiptIdsCommand))]
    private bool _isBusy;

    partial void OnSelectedStatusFilterChanged(string value) => _ = RefreshAsync();

    partial void OnSelectedQueueItemChanged(QueueItemViewModel? value)
    {
        // Re-evaluate toolbar CanExecute whenever the highlighted row changes.
        // Do not touch StatusMessage here — refresh/restore selection would wipe action results.
        NotifyActionCommands();
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        if (!IsBusy)
        {
            _ = RefreshAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunIdleCommand))]
    private async Task RefreshAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshGate, 1) == 1)
        {
            return;
        }

        try
        {
            var selectedId = SelectedQueueItem?.Id;
            var counts = await _queueRepository.GetStatusCountsAsync().ConfigureAwait(true);
            var filter = SelectedStatusFilter.Equals("All", StringComparison.OrdinalIgnoreCase)
                ? null
                : SelectedStatusFilter;
            var items = await _queueRepository.GetItemsAsync(filter, take: 250).ConfigureAwait(true);
            var rows = items
                .OrderByDescending(x => x.Id)
                .Select(QueueItemViewModel.FromEntity)
                .ToList();

            ApplyOnUi(() =>
            {
                PendingCount = counts.GetValueOrDefault(OfflineQueueStatuses.Pending);
                SyncingCount = counts.GetValueOrDefault(OfflineQueueStatuses.Syncing);
                SyncedCount = counts.GetValueOrDefault(OfflineQueueStatuses.Synced);
                QuarantinedCount = counts.GetValueOrDefault(OfflineQueueStatuses.Quarantined);

                QueueItems.Clear();
                foreach (var row in rows)
                {
                    QueueItems.Add(row);
                }

                if (selectedId is int id)
                {
                    SelectedQueueItem = QueueItems.FirstOrDefault(x => x.Id == id);
                }
            });
        }
        catch (Exception ex)
        {
            ApplyOnUi(() => StatusMessage = $"Queue refresh failed: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _refreshGate, 0);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunIdleCommand))]
    private async Task SyncNextAsync()
    {
        if (!BeginBusy("Processing next FIFO queue item…"))
        {
            return;
        }

        try
        {
            if (!_offlineTransactionSyncService.CanUploadNow())
            {
                StatusMessage = "MRA is unreachable — offline sync will resume automatically when connectivity is restored.";
                await RefreshAsync().ConfigureAwait(true);
                return;
            }

            var result = await _offlineTransactionSyncService
                .ProcessNextCompliantAsync()
                .ConfigureAwait(true);
            StatusMessage = result is null
                ? "No eligible queue item."
                : result.IsQuarantined
                    ? $"Queue item quarantined: {result.Remark}"
                    : result.SubmittedOnline
                        ? $"Uploaded offline sale {result.InvoiceNumber} to MRA (offlineSignature transmitted)."
                        : $"Processed queue item {result.QueueId}.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sync next failed: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunIdleCommand))]
    private async Task FixReceiptIdsAsync()
    {
        if (!BeginBusy("Repairing receipt invoice numbers…"))
        {
            return;
        }

        try
        {
            var result = await _offlineSalesQueueService
                .RepairAllReceiptIdentifiersAsync()
                .ConfigureAwait(true);
            StatusMessage = result.SummaryMessage;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Receipt ID repair failed: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteRetry))]
    private async Task RetryQuarantinedAsync(QueueItemViewModel? item)
    {
        item = ResolveTarget(item);
        if (item is null || !item.CanRetry)
        {
            StatusMessage = "Select a quarantined item to retry.";
            NotifyActionCommands();
            return;
        }

        if (!BeginBusy($"Retrying queue item {item.Id}…"))
        {
            return;
        }

        try
        {
            SelectedQueueItem = item;
            // ForceSyncQueueItemAsync already moves Quarantined → Pending then submits to MRA.
            var result = await _offlineSalesQueueService.ForceSyncQueueItemAsync(item.Id).ConfigureAwait(true);
            StatusMessage = FormatSyncResult(item.Id, item.InvoiceNumber, result, prefix: "Retry");
            await DrainFifoQueueAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Retry failed: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand(CanExecute = nameof(CanExecuteForceSync))]
    private async Task ForceSyncAsync(QueueItemViewModel? item)
    {
        item = ResolveTarget(item);
        if (item is null || !item.CanForceSync)
        {
            StatusMessage = "Select a quarantined or pending item to force sync.";
            NotifyActionCommands();
            return;
        }

        if (!BeginBusy($"Force-syncing invoice {item.InvoiceNumber} (#{item.Id})…"))
        {
            return;
        }

        try
        {
            SelectedQueueItem = item;
            var result = await _offlineSalesQueueService.ForceSyncQueueItemAsync(item.Id).ConfigureAwait(true);
            StatusMessage = FormatSyncResult(item.Id, item.InvoiceNumber, result, prefix: "Force sync");
            await DrainFifoQueueAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Force sync failed: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>
    /// Continues processing eligible PENDING items in FIFO order (auto-print on each success).
    /// </summary>
    private async Task DrainFifoQueueAsync(int maxItems = 25)
    {
        if (!_offlineTransactionSyncService.CanUploadNow())
        {
            StatusMessage = "MRA is unreachable — queue drain paused until connectivity returns.";
            return;
        }

        var drain = await _offlineTransactionSyncService.DrainPendingAsync().ConfigureAwait(true);
        if (drain.ProcessedCount == 0)
        {
            return;
        }

        StatusMessage =
            $"Drained {drain.ProcessedCount} offline sale(s) " +
            $"(submitted={drain.SubmittedCount}, quarantined={drain.QuarantinedCount}).";
    }

    private static string FormatSyncResult(int queueId, string invoiceNumber, SaleQueueResult? result, string prefix)
    {
        return result switch
        {
            null => $"{prefix}: queue item {queueId} not found.",
            { SubmittedOnline: true } =>
                $"{prefix} succeeded for invoice {result.InvoiceNumber}. Fiscal receipt/QR prints when MRA returns a signature.",
            { IsQuarantined: true } => $"{prefix} quarantined item {queueId}: {result.Remark}",
            _ => result.Remark ?? $"{prefix} did not complete for item {queueId} ({invoiceNumber})."
        };
    }

    [RelayCommand(CanExecute = nameof(CanExecutePrintReceipt))]
    private async Task PrintReceiptAsync(QueueItemViewModel? item)
    {
        item = ResolveTarget(item);
        if (item is null || !item.CanPrintReceipt)
        {
            StatusMessage = "Select a queue item with a printable payload (synced, pending, or quarantined).";
            NotifyActionCommands();
            return;
        }

        if (!BeginBusy($"Printing receipt for {item.InvoiceNumber}…"))
        {
            return;
        }

        try
        {
            SelectedQueueItem = item;
            if (string.IsNullOrWhiteSpace(item.PayloadJson))
            {
                StatusMessage = $"Queue item {item.Id} has an empty payload.";
                return;
            }

            var payload = JsonSerializer.Deserialize<SubmitSalesTransactionRequest>(item.PayloadJson, MraJson.SerializerOptions);
            if (payload is null)
            {
                StatusMessage = $"Unable to parse invoice payload for item {item.Id}.";
                return;
            }

            var fiscal = item.ResolveFiscalResponse();
            if (fiscal is null && !string.IsNullOrWhiteSpace(payload.InvoiceHeader.InvoiceNumber))
            {
                try
                {
                    var lookup = await _salesTransactionService
                        .GetInvoiceByNumberAsync(
                            new InvoiceNumberQueryRequest { InvoiceNumber = payload.InvoiceHeader.InvoiceNumber })
                        .ConfigureAwait(true);
                    if (lookup.Success && lookup.Data is not null)
                    {
                        var invoiceNumber = lookup.Data.InvoiceHeader?.InvoiceNumber
                            ?? payload.InvoiceHeader.InvoiceNumber;
                        fiscal = new SubmitSalesTransactionResponseData
                        {
                            InvoiceNumber = invoiceNumber,
                            FiscalSignature = invoiceNumber,
                            ValidationUrl = lookup.Data.ValidationUrl,
                            VerificationUrl = lookup.Data.ValidationUrl
                        };
                    }
                }
                catch
                {
                    // Offline / unreachable MRA — fall through to local fiscal / placeholder.
                }
            }

            if (fiscal is null && !string.IsNullOrWhiteSpace(payload.InvoiceSummary.OfflineSignature))
            {
                // Rebuild the official offline ValidationURL so pending reprints include a scannable MRA QR.
                try
                {
                    var signed = await _offlineReceiptSignatureService.SignAsync(payload).ConfigureAwait(true);
                    fiscal = new SubmitSalesTransactionResponseData
                    {
                        InvoiceNumber = payload.InvoiceHeader.InvoiceNumber,
                        FiscalSignature = signed.OfflineDataSignature,
                        ValidationUrl = signed.ValidationUrl,
                        VerificationUrl = signed.ValidationUrl
                    };
                }
                catch
                {
                    fiscal = new SubmitSalesTransactionResponseData
                    {
                        InvoiceNumber = payload.InvoiceHeader.InvoiceNumber,
                        FiscalSignature = payload.InvoiceSummary.OfflineSignature
                    };
                }
            }

            if (fiscal is null &&
                item.Status.Equals(OfflineQueueStatuses.Synced, StringComparison.OrdinalIgnoreCase) == false)
            {
                // Still pending/quarantined without a stored offline signature — try one more sign attempt.
                try
                {
                    var signed = await _offlineReceiptSignatureService.SignAsync(payload).ConfigureAwait(true);
                    fiscal = new SubmitSalesTransactionResponseData
                    {
                        InvoiceNumber = payload.InvoiceHeader.InvoiceNumber,
                        FiscalSignature = signed.OfflineDataSignature,
                        ValidationUrl = signed.ValidationUrl,
                        VerificationUrl = signed.ValidationUrl
                    };
                }
                catch
                {
                    fiscal = new SubmitSalesTransactionResponseData
                    {
                        InvoiceNumber = payload.InvoiceHeader.InvoiceNumber,
                        FiscalSignature = FiscalReceiptEnricher.OfflinePendingPlaceholder
                    };
                }
            }

            var context = await _posConfigurationService.GetRuntimeContextAsync().ConfigureAwait(true);
            await _receiptPrintingService
                .PrintAsync(QueueReceiptPrintHelper.CreatePrintRequest(context, payload, fiscal))
                .ConfigureAwait(true);
            StatusMessage = $"Printed receipt for {payload.InvoiceHeader.InvoiceNumber} (queue #{item.Id}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Print failed: {ex.Message}";
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>
    /// Prefers the explicit command parameter (row button or toolbar SelectedItem),
    /// then falls back to <see cref="SelectedQueueItem"/>.
    /// </summary>
    private QueueItemViewModel? ResolveTarget(QueueItemViewModel? item)
    {
        if (item is not null)
        {
            return item;
        }

        return SelectedQueueItem;
    }

    /// <summary>Resolves which queue row a toolbar/row action should target.</summary>
    public static QueueItemViewModel? ResolveTargetForTest(
        QueueItemViewModel? commandParameter,
        QueueItemViewModel? selectedQueueItem) =>
        commandParameter ?? selectedQueueItem;

    private bool CanRunIdleCommand() => !IsBusy;

    private bool CanExecutePrintReceipt(QueueItemViewModel? item) =>
        !IsBusy && ResolveTarget(item)?.CanPrintReceipt == true;

    private bool CanExecuteRetry(QueueItemViewModel? item) =>
        !IsBusy && ResolveTarget(item)?.CanRetry == true;

    private bool CanExecuteForceSync(QueueItemViewModel? item) =>
        !IsBusy && ResolveTarget(item)?.CanForceSync == true;

    private bool BeginBusy(string message)
    {
        if (IsBusy)
        {
            return false;
        }

        IsBusy = true;
        StatusMessage = message;
        return true;
    }

    private void EndBusy()
    {
        IsBusy = false;
        NotifyActionCommands();
    }

    private void NotifyActionCommands()
    {
        PrintReceiptCommand.NotifyCanExecuteChanged();
        RetryQuarantinedCommand.NotifyCanExecuteChanged();
        ForceSyncCommand.NotifyCanExecuteChanged();
        SyncNextCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        FixReceiptIdsCommand.NotifyCanExecuteChanged();
    }

    private static void ApplyOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
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

    /// <summary>
    /// UI label: internal queue Id stays numeric; MRA invoice number is the Base64 composite.
    /// </summary>
    public string ReceiptDisplay =>
        string.IsNullOrWhiteSpace(InvoiceNumber)
            ? $"#{Id}"
            : $"#{Id} · {InvoiceNumber}";

    public bool IsMraCompositeInvoiceNumber =>
        PointOfSale.Mra.Billing.MraInvoiceNumberGenerator.IsMraCompositeInvoiceNumber(InvoiceNumber);

    public bool CanRetry =>
        Status.Equals(OfflineQueueStatuses.Quarantined, StringComparison.OrdinalIgnoreCase);

    public bool CanForceSync =>
        Status.Equals(OfflineQueueStatuses.Quarantined, StringComparison.OrdinalIgnoreCase) ||
        Status.Equals(OfflineQueueStatuses.Pending, StringComparison.OrdinalIgnoreCase) ||
        Status.Equals(OfflineQueueStatuses.Syncing, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Print is available whenever a local sale payload exists (synced fiscal receipt or offline placeholder).
    /// </summary>
    public bool CanPrintReceipt =>
        !string.IsNullOrWhiteSpace(PayloadJson) &&
        (Status.Equals(OfflineQueueStatuses.Synced, StringComparison.OrdinalIgnoreCase) ||
         Status.Equals(OfflineQueueStatuses.Pending, StringComparison.OrdinalIgnoreCase) ||
         Status.Equals(OfflineQueueStatuses.Quarantined, StringComparison.OrdinalIgnoreCase));

    public SubmitSalesTransactionResponseData? ResolveFiscalResponse()
    {
        if (string.IsNullOrWhiteSpace(FiscalResponseJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SubmitSalesTransactionResponseData>(
                FiscalResponseJson,
                MraJson.SerializerOptions);
        }
        catch
        {
            return null;
        }
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
