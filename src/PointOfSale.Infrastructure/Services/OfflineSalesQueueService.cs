using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Infrastructure.Services;

public sealed class OfflineSalesQueueService
{
    private readonly IOfflineInvoiceQueueRepository _queueRepository;
    private readonly SalesTransactionService _salesTransactionService;
    private readonly OfflineSyncOptions _options;
    private readonly ILogger<OfflineSalesQueueService> _logger;

    public OfflineSalesQueueService(
        IOfflineInvoiceQueueRepository queueRepository,
        SalesTransactionService salesTransactionService,
        IOptions<OfflineSyncOptions> options,
        ILogger<OfflineSalesQueueService> logger)
    {
        _queueRepository = queueRepository;
        _salesTransactionService = salesTransactionService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SaleQueueResult> EnqueueAndTrySubmitAsync(
        SubmitSalesTransactionRequest request,
        bool forceOffline,
        CancellationToken cancellationToken = default)
    {
        await _salesTransactionService.ValidateSaleAgainstInventoryAsync(request, cancellationToken)
            .ConfigureAwait(false);

        var payload = await PreparePayloadAsync(request, forceOffline, cancellationToken).ConfigureAwait(false);
        var payloadJson = JsonSerializer.Serialize(payload, MraJson.SerializerOptions);
        var queueId = await _queueRepository.EnqueuePendingAsync(payloadJson, cancellationToken).ConfigureAwait(false);

        if (forceOffline)
        {
            return SaleQueueResult.Queued(queueId, payload.InvoiceHeader.InvoiceNumber, submittedOnline: false);
        }

        return await TrySubmitQueuedAsync(queueId, payload, currentRetryCount: 0, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ProcessNextFifoAsync(CancellationToken cancellationToken = default)
    {
        var next = await _queueRepository.GetNextFifoEligibleAsync(cancellationToken).ConfigureAwait(false);
        if (next is null)
        {
            return false;
        }

        if (!await _queueRepository.TryMarkSyncingAsync(next.Id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        SubmitSalesTransactionRequest payload;
        try
        {
            payload = JsonSerializer.Deserialize<SubmitSalesTransactionRequest>(next.PayloadJson, MraJson.SerializerOptions)
                ?? throw new InvalidOperationException($"Queue item {next.Id} payload is invalid.");
        }
        catch (Exception ex)
        {
            await _queueRepository
                .MarkQuarantinedAsync(next.Id, TruncateError($"Invalid payload: {ex.Message}"), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        await TrySubmitQueuedAsync(next.Id, payload, next.RetryCount, cancellationToken, alreadySyncing: true)
            .ConfigureAwait(false);
        return true;
    }

    private async Task<SaleQueueResult> TrySubmitQueuedAsync(
        int queueId,
        SubmitSalesTransactionRequest payload,
        int currentRetryCount,
        CancellationToken cancellationToken,
        bool alreadySyncing = false)
    {
        try
        {
            var submit = await _salesTransactionService
                .SubmitSalesTransactionAsync(payload, cancellationToken)
                .ConfigureAwait(false);

            if (submit.Success)
            {
                await _queueRepository.MarkSyncedAsync(queueId, cancellationToken).ConfigureAwait(false);
                await _salesTransactionService.ApplyLocalInventoryDeductionsAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
                return SaleQueueResult.Submitted(queueId, payload.InvoiceHeader.InvoiceNumber, submit.Data, submit.Remark);
            }

            if (IsPermanentBusinessFailure(submit))
            {
                await _queueRepository
                    .MarkQuarantinedAsync(queueId, TruncateError(submit.Remark ?? "MRA rejected sale."), cancellationToken)
                    .ConfigureAwait(false);
                return SaleQueueResult.Quarantined(queueId, payload.InvoiceHeader.InvoiceNumber, submit.Remark ?? "Rejected");
            }

            await ScheduleRetryAsync(
                    new OfflineInvoiceQueueItem
                    {
                        Id = queueId,
                        PayloadJson = string.Empty,
                        CreatedAt = DateTime.UtcNow,
                        Status = OfflineQueueStatuses.Pending,
                        RetryCount = currentRetryCount
                    },
                    submit.Remark ?? "MRA sale submission failed.",
                    cancellationToken)
                .ConfigureAwait(false);

            return SaleQueueResult.Queued(queueId, payload.InvoiceHeader.InvoiceNumber, submittedOnline: false, submit.Remark);
        }
        catch (MraApiException ex) when (ex.HttpStatusCode == 400)
        {
            await _queueRepository.MarkQuarantinedAsync(queueId, TruncateError(ex.Message), cancellationToken)
                .ConfigureAwait(false);
            _logger.LogWarning("Quarantined queue id {QueueId} after HTTP 400: {Error}", queueId, ex.Message);
            return SaleQueueResult.Quarantined(queueId, payload.InvoiceHeader.InvoiceNumber, ex.Message);
        }
        catch (Exception ex) when (IsTransientFailure(ex))
        {
            await ScheduleRetryAsync(
                    new OfflineInvoiceQueueItem
                    {
                        Id = queueId,
                        PayloadJson = string.Empty,
                        CreatedAt = DateTime.UtcNow,
                        Status = OfflineQueueStatuses.Syncing,
                        RetryCount = currentRetryCount
                    },
                    ex.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return SaleQueueResult.Queued(queueId, payload.InvoiceHeader.InvoiceNumber, submittedOnline: false, ex.Message);
        }
    }

    private async Task<SubmitSalesTransactionRequest> PreparePayloadAsync(
        SubmitSalesTransactionRequest request,
        bool forceOffline,
        CancellationToken cancellationToken)
    {
        if (!forceOffline)
        {
            return request;
        }

        var unsigned = request with
        {
            InvoiceSummary = request.InvoiceSummary with { OfflineSignature = null }
        };
        var signaturePayload = JsonSerializer.Serialize(unsigned, MraJson.SerializerOptions);
        var offlineSignature = await _salesTransactionService
            .ComputeOfflineSignatureAsync(signaturePayload, cancellationToken)
            .ConfigureAwait(false);

        return request with
        {
            InvoiceSummary = request.InvoiceSummary with { OfflineSignature = offlineSignature }
        };
    }

    private async Task ScheduleRetryAsync(OfflineInvoiceQueueItem item, string error, CancellationToken cancellationToken)
    {
        var retryCount = item.RetryCount + 1;
        if (retryCount > _options.MaxRetryAttempts)
        {
            await _queueRepository
                .MarkQuarantinedAsync(item.Id, TruncateError($"Max retries exceeded. Last error: {error}"), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var nextRetry = ComputeNextRetryUtc(retryCount - 1);
        await _queueRepository
            .ResetSyncingToPendingAsync(item.Id, retryCount, nextRetry, TruncateError(error), cancellationToken)
            .ConfigureAwait(false);
    }

    private DateTime ComputeNextRetryUtc(int currentRetryCount)
    {
        var exponent = Math.Min(currentRetryCount, 10);
        var delaySeconds = Math.Min(
            _options.BaseBackoffSeconds * Math.Pow(2, exponent),
            _options.MaxBackoffSeconds);
        return DateTime.UtcNow.AddSeconds(delaySeconds);
    }

    private static bool IsTransientFailure(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or MraApiException { HttpStatusCode: >= 500 };

    private static bool IsPermanentBusinessFailure(SalesResult<SubmitSalesTransactionResponseData> submit) =>
        submit.Errors?.Any(e => e.ErrorCode is >= 40000 and < 50000) == true;

    private static string TruncateError(string message) =>
        message.Length <= 4000 ? message : message[..4000];
}

public sealed class SaleQueueResult
{
    public int QueueId { get; init; }
    public required string InvoiceNumber { get; init; }
    public bool SubmittedOnline { get; init; }
    public bool IsQuarantined { get; init; }
    public string? Remark { get; init; }
    public SubmitSalesTransactionResponseData? Response { get; init; }

    public static SaleQueueResult Submitted(
        int queueId,
        string invoiceNumber,
        SubmitSalesTransactionResponseData? response,
        string? remark) =>
        new()
        {
            QueueId = queueId,
            InvoiceNumber = invoiceNumber,
            SubmittedOnline = true,
            Response = response,
            Remark = remark
        };

    public static SaleQueueResult Queued(int queueId, string invoiceNumber, bool submittedOnline, string? remark = null) =>
        new()
        {
            QueueId = queueId,
            InvoiceNumber = invoiceNumber,
            SubmittedOnline = submittedOnline,
            Remark = remark
        };

    public static SaleQueueResult Quarantined(int queueId, string invoiceNumber, string remark) =>
        new()
        {
            QueueId = queueId,
            InvoiceNumber = invoiceNumber,
            IsQuarantined = true,
            Remark = remark
        };
}
