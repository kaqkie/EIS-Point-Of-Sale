using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Compliance;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Infrastructure.Services;

public sealed class OfflineSalesQueueService
{
    private readonly IOfflineInvoiceQueueRepository _queueRepository;
    private readonly SalesTransactionService _salesTransactionService;
    private readonly IConfigurationRepository? _configurationRepository;
    private readonly TerminalOnboardingService? _terminalOnboardingService;
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
        MraRuntimeEnvironmentState? runtimeState = null,
        IConfigurationRepository? configurationRepository = null,
        TerminalOnboardingService? terminalOnboardingService = null)
    {
        _queueRepository = queueRepository;
        _salesTransactionService = salesTransactionService;
        _options = options.Value;
        _logger = logger;
        _syncCompletedHandler = syncCompletedHandler;
        _complianceAudit = complianceAudit;
        _runtimeState = runtimeState;
        _configurationRepository = configurationRepository;
        _terminalOnboardingService = terminalOnboardingService;
    }

    public async Task<SaleQueueResult> EnqueueAndTrySubmitAsync(
        SubmitSalesTransactionRequest request,
        bool forceOffline,
        CancellationToken cancellationToken = default)
    {
        var identity = await LoadFiscalIdentityOverlayAsync(cancellationToken).ConfigureAwait(false);
        request = NormalizeQueuedPayloadForResubmit(request, identity);

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

            // HTTP was successful, but MRA returned success=false (logical failure).
            // There is no transport exception here, so log full validation signals.
            var errorsJson = submit.Errors is null
                ? "(no errors array)"
                : JsonSerializer.Serialize(submit.Errors, MraJson.SerializerOptions);
            _logger.LogWarning(
                "MRA EIS submission returned success=false for queue {QueueId} invoice {InvoiceNumber}. Remark={Remark}. Errors={ErrorsJson}.",
                queueId,
                payload.InvoiceHeader.InvoiceNumber,
                submit.Remark ?? "(null)",
                errorsJson);

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
                // Local log visibility: even though MraApiClient logs RequestPayload, this ensures
                // we also capture the exact serialized JSON that this queue item sent.
                string? outgoingJson = null;
                try
                {
                    outgoingJson = JsonSerializer.Serialize(payload, MraJson.SerializerOptions);
                }
                catch
                {
                    // Never let logging break the quarantine path.
                }

                var outgoingJsonForLog = outgoingJson is null
                    ? "(unable to serialize outgoing payload)"
                    : outgoingJson.Length <= 100_000
                        ? outgoingJson
                        : outgoingJson[..100_000] + "...(truncated)";

                _logger.LogError(ex,
                    "Opaque sandbox rejection for queue {QueueId}. OutgoingPayloadJson={OutgoingPayloadJson}",
                    queueId,
                    outgoingJsonForLog);

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

        var identity = await LoadFiscalIdentityOverlayAsync(cancellationToken).ConfigureAwait(false);
        var hadOfflineSignature = !string.IsNullOrWhiteSpace(deserialized.InvoiceSummary.OfflineSignature);
        var normalized = NormalizeQueuedPayloadForResubmit(deserialized, identity);
        if (hadOfflineSignature)
        {
            normalized = await RefreshOfflineSignatureAsync(normalized, cancellationToken).ConfigureAwait(false);
        }

        var normalizedJson = JsonSerializer.Serialize(normalized, MraJson.SerializerOptions);
        if (!string.Equals(payloadJson.Trim(), normalizedJson, StringComparison.Ordinal))
        {
            await _queueRepository.UpdatePayloadJsonAsync(queueId, normalizedJson, cancellationToken)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Normalized queue payload for item {QueueId} before MRA resubmit (length {Before} -> {After}). sellerTIN={Tin} siteId={SiteId} taxRates={TaxRates}",
                queueId,
                payloadJson.Length,
                normalizedJson.Length,
                normalized.InvoiceHeader.SellerTin,
                normalized.InvoiceHeader.SiteId,
                string.Join(",", normalized.InvoiceLineItems.Select(l => l.TaxRateId).Distinct()));
        }

        return normalized;
    }

    /// <summary>
    /// Round-trips through the current serializer and aligns MRA fiscal fields
    /// (site codes, taxRateId A, config versions, 2-dp money) for Force Sync / Retry.
    /// </summary>
    public static SubmitSalesTransactionRequest NormalizeQueuedPayloadForResubmit(
        SubmitSalesTransactionRequest request,
        MraFiscalIdentityOverlay? identity = null) =>
        MraFiscalPayloadNormalizer.Normalize(request, identity);

    private async Task<MraFiscalIdentityOverlay?> LoadFiscalIdentityOverlayAsync(CancellationToken cancellationToken)
    {
        if (_configurationRepository is null)
        {
            return new MraFiscalIdentityOverlay(StandardTaxRateId: MraTaxRateCodes.StandardVat);
        }

        try
        {
            var globalJson = await _configurationRepository
                .GetJsonAsync(MraConfigurationKeys.GlobalConfiguration, cancellationToken)
                .ConfigureAwait(false);
            var terminalJson = await _configurationRepository
                .GetJsonAsync(MraConfigurationKeys.TerminalConfiguration, cancellationToken)
                .ConfigureAwait(false);
            var taxpayerJson = await _configurationRepository
                .GetJsonAsync(MraConfigurationKeys.TaxpayerConfiguration, cancellationToken)
                .ConfigureAwait(false);
            var siteOverride = await _configurationRepository
                .GetJsonAsync(DeploymentConfigurationKeys.SiteIdOverride, cancellationToken)
                .ConfigureAwait(false);
            var tinOverride = await _configurationRepository
                .GetJsonAsync(DeploymentConfigurationKeys.TaxpayerTin, cancellationToken)
                .ConfigureAwait(false);

            var global = string.IsNullOrWhiteSpace(globalJson)
                ? null
                : JsonSerializer.Deserialize<GlobalConfigurationDto>(globalJson, MraJson.SerializerOptions);
            var terminal = string.IsNullOrWhiteSpace(terminalJson)
                ? null
                : JsonSerializer.Deserialize<TerminalConfigurationDto>(terminalJson, MraJson.SerializerOptions);
            var taxpayer = string.IsNullOrWhiteSpace(taxpayerJson)
                ? null
                : JsonSerializer.Deserialize<TaxpayerConfigurationDto>(taxpayerJson, MraJson.SerializerOptions);

            var rates = global?.TaxRates?
                .Where(r => !string.IsNullOrWhiteSpace(r.Id) && r.Rate > 0m)
                .Select(r => (Id: r.Id!.Trim(), Rate: r.Rate))
                .ToList();

            var standardFromRate = rates?.FirstOrDefault(r => r.Rate is >= 16m and <= 18m);
            var standardId = !string.IsNullOrWhiteSpace(standardFromRate?.Id)
                ? standardFromRate.Value.Id
                : taxpayer?.ActivatedTaxRateIds?.FirstOrDefault(id =>
                    !string.IsNullOrWhiteSpace(id)
                    && id.Trim().Equals(MraTaxRateCodes.StandardVat, StringComparison.OrdinalIgnoreCase))
                ?? MraTaxRateCodes.StandardVat;

            var siteId = FirstNonEmpty(
                terminal?.TerminalSite?.SiteId,
                ExtractConfiguredString(siteOverride));
            var sellerTin = FirstNonEmpty(
                taxpayer?.Tin,
                ExtractConfiguredString(tinOverride));

            // Prefer activation JWT TIN claim over the sandbox developer seed.
            const string sandboxPlaceholderTin = "1234567890";
            if (string.IsNullOrWhiteSpace(sellerTin) ||
                sellerTin.Trim().Equals(sandboxPlaceholderTin, StringComparison.Ordinal))
            {
                var jwt = await _configurationRepository
                    .GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, cancellationToken)
                    .ConfigureAwait(false);
                var jwtTin = MraJwtClaims.TryGetTaxpayerTin(jwt);
                if (!string.IsNullOrWhiteSpace(jwtTin) &&
                    !jwtTin.Trim().Equals(sandboxPlaceholderTin, StringComparison.Ordinal))
                {
                    sellerTin = jwtTin.Trim();
                }
            }

            // If cached identity/config looks incomplete (versions missing or values still placeholders),
            // refresh configs from the activated terminal so invoiceHeader.{sellerTIN,siteId} and
            // config versions match MRA terminal activation.
            var isNonCodeLikeSiteId = siteId?.Contains(' ') == true;
            var sellerTinIsPlaceholder = sellerTin?.Trim().Equals(sandboxPlaceholderTin, StringComparison.Ordinal) == true;

            // Pull active sandbox-activated values when we detect clear placeholders / display labels.
            var needsRefresh = isNonCodeLikeSiteId || sellerTinIsPlaceholder;

            if (needsRefresh && _terminalOnboardingService is not null)
            {
                try
                {
                    var latest = await _terminalOnboardingService
                        .GetLatestConfigsAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (latest.Success && latest.Configuration is not null)
                    {
                        var bundle = latest.Configuration;
                        var refreshedGlobal = bundle.GlobalConfiguration;
                        var refreshedTerminal = bundle.TerminalConfiguration;
                        var refreshedTaxpayer = bundle.TaxpayerConfiguration;

                        var refreshedRates = refreshedGlobal?.TaxRates?
                            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && r.Rate > 0m)
                            .Select(r => (r.Id!.Trim(), r.Rate))
                            .ToList();

                        var activatedStandard = refreshedTaxpayer?.ActivatedTaxRateIds?
                            .FirstOrDefault(id =>
                                !string.IsNullOrWhiteSpace(id)
                                && id.Trim().Equals(MraTaxRateCodes.StandardVat, StringComparison.OrdinalIgnoreCase));

                        var refreshedStandardId = !string.IsNullOrWhiteSpace(activatedStandard)
                            ? activatedStandard.Trim()
                            : MraTaxRateCodes.StandardVat;

                        var refreshedSiteId = FirstNonEmpty(refreshedTerminal?.TerminalSite?.SiteId);
                        var refreshedSellerTin = FirstNonEmpty(refreshedTaxpayer?.Tin);

                        return new MraFiscalIdentityOverlay(
                            SellerTin: refreshedSellerTin ?? sellerTin,
                            SiteId: refreshedSiteId ?? siteId,
                            GlobalConfigVersion: refreshedGlobal?.VersionNo ?? global?.VersionNo ?? 1,
                            TaxpayerConfigVersion: refreshedTaxpayer?.VersionNo ?? taxpayer?.VersionNo ?? 1,
                            TerminalConfigVersion: refreshedTerminal?.VersionNo ?? terminal?.VersionNo ?? 1,
                            StandardTaxRateId: refreshedStandardId,
                            ConfiguredTaxRates: refreshedRates);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed refreshing latest MRA configs for payload normalize; falling back to cached identity.");
                }
            }

            return new MraFiscalIdentityOverlay(
                SellerTin: sellerTin,
                SiteId: siteId,
                GlobalConfigVersion: global?.VersionNo,
                TaxpayerConfigVersion: taxpayer?.VersionNo,
                TerminalConfigVersion: terminal?.VersionNo,
                StandardTaxRateId: standardId,
                ConfiguredTaxRates: rates);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load fiscal identity overlay for payload normalize; using defaults.");
            return new MraFiscalIdentityOverlay(StandardTaxRateId: MraTaxRateCodes.StandardVat);
        }
    }

    private static string? ExtractConfiguredString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                return NullIfWhiteSpace(doc.RootElement.GetString());
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = NullIfWhiteSpace(property.Value.GetString());
                        if (value is not null)
                        {
                            return value;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            return NullIfWhiteSpace(trimmed.Trim('"'));
        }

        return null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
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

        return await RefreshOfflineSignatureAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SubmitSalesTransactionRequest> RefreshOfflineSignatureAsync(
        SubmitSalesTransactionRequest request,
        CancellationToken cancellationToken)
    {
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
