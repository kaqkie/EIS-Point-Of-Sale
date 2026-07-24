using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Compliance;
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
    private readonly IOfflineInvoiceSyncCompletedHandler? _syncCompletedHandler;
    private readonly IComplianceAuditLogger? _complianceAudit;
    private readonly MraRuntimeEnvironmentState? _runtimeState;
    private readonly OfflineSyncOptions _options;
    private readonly ILogger<OfflineSalesQueueService> _logger;

    public OfflineSalesQueueService(
        IOfflineInvoiceQueueRepository queueRepository,
        SalesTransactionService salesTransactionService,
        IOptions<OfflineSyncOptions> options,
        ILogger<OfflineSalesQueueService> logger,
        IOfflineInvoiceSyncCompletedHandler? syncCompletedHandler = null,
        IComplianceAuditLogger? complianceAudit = null,
        MraRuntimeEnvironmentState? runtimeState = null)
    {
        _queueRepository = queueRepository;
        _salesTransactionService = salesTransactionService;
        _options = options.Value;
        _logger = logger;
        _syncCompletedHandler = syncCompletedHandler;
        _complianceAudit = complianceAudit;
        _runtimeState = runtimeState;
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
        await LogComplianceAsync(
                ComplianceAuditCategories.OfflineQueue,
                "EnqueuePending",
                $"Invoice {payload.InvoiceHeader.InvoiceNumber} queued (id {queueId}).",
                success: true,
                correlationId: queueId.ToString(),
                cancellationToken)
            .ConfigureAwait(false);

        if (forceOffline)
        {
            return SaleQueueResult.Queued(queueId, payload.InvoiceHeader.InvoiceNumber, submittedOnline: false);
        }

        return await TrySubmitQueuedAsync(
            queueId,
            payload,
            currentRetryCount: 0,
            cancellationToken,
            triggerAutoPrintOnSuccess: false).ConfigureAwait(false);
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
            payload = await LoadAndNormalizePayloadAsync(next.Id, next.PayloadJson, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _queueRepository
                .MarkQuarantinedAsync(next.Id, TruncateError($"Invalid payload: {ex.Message}"), cancellationToken)
                .ConfigureAwait(false);
            return true;
        }

        await TrySubmitQueuedAsync(
            next.Id,
            payload,
            next.RetryCount,
            cancellationToken,
            triggerAutoPrintOnSuccess: true).ConfigureAwait(false);
        return true;
    }

    public async Task<SaleQueueResult?> ForceSyncQueueItemAsync(int queueId, CancellationToken cancellationToken = default)
    {
        var item = await _queueRepository.GetByIdAsync(queueId, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        if (item.Status.Equals(OfflineQueueStatuses.Quarantined, StringComparison.OrdinalIgnoreCase))
        {
            var retried = await _queueRepository.RetryQuarantinedAsync(queueId, cancellationToken).ConfigureAwait(false);
            if (!retried)
            {
                return SaleQueueResult.Quarantined(queueId, string.Empty, "Item is not quarantined or could not be retried.");
            }

            item = await _queueRepository.GetByIdAsync(queueId, cancellationToken).ConfigureAwait(false);
        }

        if (item is null ||
            !item.Status.Equals(OfflineQueueStatuses.Pending, StringComparison.OrdinalIgnoreCase))
        {
            return SaleQueueResult.Queued(queueId, string.Empty, submittedOnline: false, "Item is not eligible for force sync.");
        }

        if (!await _queueRepository.TryMarkSyncingAsync(queueId, cancellationToken).ConfigureAwait(false))
        {
            return SaleQueueResult.Queued(queueId, string.Empty, submittedOnline: false, "Item is already syncing.");
        }

        SubmitSalesTransactionRequest payload;
        try
        {
            payload = await LoadAndNormalizePayloadAsync(queueId, item.PayloadJson, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _queueRepository
                .MarkQuarantinedAsync(queueId, TruncateError($"Invalid payload: {ex.Message}"), cancellationToken)
                .ConfigureAwait(false);
            return SaleQueueResult.Quarantined(queueId, string.Empty, ex.Message);
        }

        return await TrySubmitQueuedAsync(
            queueId,
            payload,
            item.RetryCount,
            cancellationToken,
            triggerAutoPrintOnSuccess: true).ConfigureAwait(false);
    }

    private async Task<SaleQueueResult> TrySubmitQueuedAsync(
        int queueId,
        SubmitSalesTransactionRequest payload,
        int currentRetryCount,
        CancellationToken cancellationToken,
        bool triggerAutoPrintOnSuccess)
    {
        try
        {
            var submit = await _salesTransactionService
                .SubmitSalesTransactionAsync(payload, cancellationToken)
                .ConfigureAwait(false);

            if (submit.Success && submit.Data is not null)
            {
                var fiscalJson = JsonSerializer.Serialize(submit.Data, MraJson.SerializerOptions);
                await _queueRepository.MarkSyncedAsync(queueId, fiscalJson, cancellationToken).ConfigureAwait(false);
                _runtimeState?.RecordSuccessfulSync(DateTime.UtcNow);
                await LogComplianceAsync(
                        ComplianceAuditCategories.TransactionSubmission,
                        "MraSubmitSuccess",
                        $"Invoice {payload.InvoiceHeader.InvoiceNumber} synced (queue {queueId}).",
                        success: true,
                        correlationId: queueId.ToString(),
                        cancellationToken)
                    .ConfigureAwait(false);
                await _salesTransactionService.ApplyLocalInventoryDeductionsAsync(payload, cancellationToken)
                    .ConfigureAwait(false);

                if (triggerAutoPrintOnSuccess && _syncCompletedHandler is not null)
                {
                    try
                    {
                        await _syncCompletedHandler
                            .HandleSuccessfulSyncAsync(payload, submit.Data, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Post-sync receipt handler failed for queue id {QueueId}.", queueId);
                    }
                }

                return SaleQueueResult.Submitted(queueId, payload.InvoiceHeader.InvoiceNumber, submit.Data, submit.Remark);
            }

            if (submit.Success)
            {
                await _queueRepository.MarkSyncedAsync(queueId, fiscalResponseJson: null, cancellationToken)
                    .ConfigureAwait(false);
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

            await LogComplianceAsync(
                    ComplianceAuditCategories.OfflineQueue,
                    "RetryScheduled",
                    $"Queue {queueId} retry #{currentRetryCount + 1}: {submit.Remark}",
                    success: false,
                    correlationId: queueId.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);

            return SaleQueueResult.Queued(queueId, payload.InvoiceHeader.InvoiceNumber, submittedOnline: false, submit.Remark);
        }
        catch (MraApiException ex) when (ex.LooksLikeValidationOrClientError())
        {
            // HTTP 400, HttpClient lifetime faults, sandbox validation 500s, and opaque
            // {"message":"An internal error occurred"} — quarantine instead of retry storm.
            var detail = TruncateError(ex.Message);
            if (ex.IsHttpClientLifetimeError())
            {
                detail = TruncateError(
                    "HttpClient lifetime fault (properties mutated after first request). " +
                    "Resubmit after upgrade; do not share/mutate a started HttpClient. " + detail);
            }
            else if (MraApiException.IsOpaqueSandboxInternalError(ex.ResponseBody))
            {
                detail = TruncateError(
                    "MRA sandbox rejected the payload (opaque internal error). " +
                    "Verify sellerTIN, siteId, taxRateId, and config versions match terminal activation. " +
                    "Full JSON was logged as RequestPayload. " + detail);
            }

            await _queueRepository.MarkQuarantinedAsync(queueId, detail, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogWarning(
                "Quarantined queue id {QueueId} after MRA HTTP {Status}: {Error}. ResponseBody={ResponseBody}",
                queueId,
                ex.HttpStatusCode,
                ex.Message,
                TruncateError(ex.ResponseBody ?? string.Empty));
            return SaleQueueResult.Quarantined(queueId, payload.InvoiceHeader.InvoiceNumber, detail);
        }
        catch (Exception ex) when (IsTransientFailure(ex))
        {
            var error = FormatQueueError(ex);
            await ScheduleRetryAsync(
                    new OfflineInvoiceQueueItem
                    {
                        Id = queueId,
                        PayloadJson = string.Empty,
                        CreatedAt = DateTime.UtcNow,
                        Status = OfflineQueueStatuses.Syncing,
                        RetryCount = currentRetryCount
                    },
                    error,
                    cancellationToken)
                .ConfigureAwait(false);
            return SaleQueueResult.Queued(queueId, payload.InvoiceHeader.InvoiceNumber, submittedOnline: false, error);
        }
        catch (Exception ex) when (IsHttpClientLifetimeFault(ex))
        {
            var detail = TruncateError(
                "HttpClient lifetime fault (properties mutated after first request). " + FormatQueueError(ex));
            await _queueRepository.MarkQuarantinedAsync(queueId, detail, cancellationToken)
                .ConfigureAwait(false);
            return SaleQueueResult.Quarantined(queueId, payload.InvoiceHeader.InvoiceNumber, detail);
        }
        catch (Exception ex)
        {
            // Never leave the item stuck in SYNCING — return to PENDING (or quarantine after max retries).
            _logger.LogError(ex, "Unexpected MRA sync failure for queue id {QueueId}; scheduling retry.", queueId);
            var error = FormatQueueError(ex);
            await ScheduleRetryAsync(
                    new OfflineInvoiceQueueItem
                    {
                        Id = queueId,
                        PayloadJson = string.Empty,
                        CreatedAt = DateTime.UtcNow,
                        Status = OfflineQueueStatuses.Syncing,
                        RetryCount = currentRetryCount
                    },
                    error,
                    cancellationToken)
                .ConfigureAwait(false);
            return SaleQueueResult.Queued(queueId, payload.InvoiceHeader.InvoiceNumber, submittedOnline: false, error);
        }
    }

    /// <summary>
    /// Deserializes the queued JSON, normalizes to the current DTO shape, and persists corrections
    /// so Force Sync / Retry resubmit the cleaned payload.
    /// </summary>
    private async Task<SubmitSalesTransactionRequest> LoadAndNormalizePayloadAsync(
        int queueId,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        var deserialized = JsonSerializer.Deserialize<SubmitSalesTransactionRequest>(payloadJson, MraJson.SerializerOptions)
            ?? throw new InvalidOperationException($"Queue item {queueId} payload is invalid.");

        var normalized = NormalizeQueuedPayloadForResubmit(deserialized);
        var normalizedJson = JsonSerializer.Serialize(normalized, MraJson.SerializerOptions);
        if (!string.Equals(payloadJson.Trim(), normalizedJson, StringComparison.Ordinal))
        {
            await _queueRepository.UpdatePayloadJsonAsync(queueId, normalizedJson, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Normalized queue payload for item {QueueId} before MRA resubmit (length {Before} -> {After}).",
                queueId,
                payloadJson.Length,
                normalizedJson.Length);
        }

        return normalized;
    }

    /// <summary>
    /// Round-trips through the current serializer and cleans empty optional strings / line ids
    /// so Force Sync and Retry send a stable payload structure.
    /// </summary>
    public static SubmitSalesTransactionRequest NormalizeQueuedPayloadForResubmit(SubmitSalesTransactionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var roundTripJson = JsonSerializer.Serialize(request, MraJson.SerializerOptions);
        var normalized = JsonSerializer.Deserialize<SubmitSalesTransactionRequest>(roundTripJson, MraJson.SerializerOptions)
            ?? request;

        var sourceHeader = normalized.InvoiceHeader;
        var header = new InvoiceHeaderDto
        {
            InvoiceNumber = sourceHeader.InvoiceNumber.Trim(),
            InvoiceDateTime = NormalizeInvoiceDateTime(sourceHeader.InvoiceDateTime),
            SellerTin = sourceHeader.SellerTin.Trim(),
            BuyerTin = NullIfWhiteSpace(sourceHeader.BuyerTin),
            BuyerName = NullIfWhiteSpace(sourceHeader.BuyerName),
            BuyerAuthorizationCode = NullIfWhiteSpace(sourceHeader.BuyerAuthorizationCode),
            SiteId = sourceHeader.SiteId.Trim(),
            GlobalConfigVersion = sourceHeader.GlobalConfigVersion,
            TaxpayerConfigVersion = sourceHeader.TaxpayerConfigVersion,
            TerminalConfigVersion = sourceHeader.TerminalConfigVersion,
            IsReliefSupply = sourceHeader.IsReliefSupply,
            Vat5CertificateDetails = sourceHeader.Vat5CertificateDetails,
            PaymentMethod = sourceHeader.PaymentMethod.Trim()
        };

        var lines = normalized.InvoiceLineItems
            .Select((line, index) => new InvoiceLineItemDto
            {
                Id = index + 1,
                ProductCode = line.ProductCode.Trim(),
                Description = line.Description.Trim(),
                UnitPrice = line.UnitPrice,
                Quantity = line.Quantity,
                Discount = line.Discount,
                Total = line.Total,
                TotalVat = line.TotalVat,
                TaxRateId = line.TaxRateId.Trim(),
                IsProduct = line.IsProduct
            })
            .ToList();

        var summary = normalized.InvoiceSummary with
        {
            OfflineSignature = NullIfWhiteSpace(normalized.InvoiceSummary.OfflineSignature),
            TaxBreakDown = normalized.InvoiceSummary.TaxBreakDown
                .Select(t => new TaxBreakDownDto
                {
                    RateId = t.RateId.Trim(),
                    TaxableAmount = t.TaxableAmount,
                    TaxAmount = t.TaxAmount
                })
                .ToList(),
            LevyBreakDown = normalized.InvoiceSummary.LevyBreakDown?
                .Select(l => new LevyBreakDownDto
                {
                    LevyTypeId = l.LevyTypeId.Trim(),
                    LevyRate = l.LevyRate,
                    LevyAmount = l.LevyAmount
                })
                .ToList()
        };

        return normalized with
        {
            InvoiceHeader = header,
            InvoiceLineItems = lines,
            InvoiceSummary = summary
        };
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

    private static bool IsTransientFailure(Exception ex)
    {
        if (IsHttpClientLifetimeFault(ex))
        {
            return false;
        }

        if (ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException)
        {
            return true;
        }

        // Pure infrastructure 5xx / transport with no validation body — retry with backoff.
        if (ex is MraApiException mra)
        {
            if (mra.LooksLikeValidationOrClientError())
            {
                return false;
            }

            return mra.HttpStatusCode is 0 or >= 500 or 408 or 429;
        }

        return ex.InnerException is HttpRequestException or TaskCanceledException or TimeoutException;
    }

    private static bool IsHttpClientLifetimeFault(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is InvalidOperationException &&
                current.Message.Contains("already started one or more requests", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPermanentBusinessFailure(SalesResult<SubmitSalesTransactionResponseData> submit) =>
        submit.Errors?.Any(e => e.ErrorCode is >= 40000 and < 50000) == true;

    private static string FormatQueueError(Exception ex)
    {
        if (ex is MraApiException mra)
        {
            return TruncateError(mra.Message);
        }

        return TruncateError(ex.Message);
    }

    private static string TruncateError(string message) =>
        message.Length <= 4000 ? message : message[..4000];

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Forces UTC and truncates to whole milliseconds so Force Sync / Retry emit the MRA datetime shape.
    /// </summary>
    public static DateTime NormalizeInvoiceDateTime(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        return new DateTime(
            utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond),
            DateTimeKind.Utc);
    }

    private Task LogComplianceAsync(
        string category,
        string action,
        string detail,
        bool success,
        string? correlationId,
        CancellationToken cancellationToken) =>
        _complianceAudit is null
            ? Task.CompletedTask
            : _complianceAudit.LogEventAsync(category, action, detail, success, correlationId, cancellationToken: cancellationToken);
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
