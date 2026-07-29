using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Orchestrates offline queue drain when MRA connectivity is restored: connectivity gate,
/// age/signature compliance, and FIFO upload of signed offline sales.
/// </summary>
public sealed class OfflineTransactionSyncService
{
    private readonly OfflineSalesQueueService _queueService;
    private readonly IOfflineInvoiceQueueRepository _queueRepository;
    private readonly IConfigurationRepository? _configurationRepository;
    private readonly IOfflineTransactionComplianceValidator _complianceValidator;
    private readonly IMraConnectivityMonitor _connectivity;
    private readonly OfflineReceiptSignatureService? _offlineReceiptSignatureService;
    private readonly OfflineSyncOptions _options;
    private readonly ILogger<OfflineTransactionSyncService> _logger;

    public OfflineTransactionSyncService(
        OfflineSalesQueueService queueService,
        IOfflineInvoiceQueueRepository queueRepository,
        IOfflineTransactionComplianceValidator complianceValidator,
        IMraConnectivityMonitor connectivity,
        IOptions<OfflineSyncOptions> options,
        ILogger<OfflineTransactionSyncService> logger,
        IConfigurationRepository? configurationRepository = null,
        OfflineReceiptSignatureService? offlineReceiptSignatureService = null)
    {
        _queueService = queueService;
        _queueRepository = queueRepository;
        _complianceValidator = complianceValidator;
        _connectivity = connectivity;
        _options = options.Value;
        _logger = logger;
        _configurationRepository = configurationRepository;
        _offlineReceiptSignatureService = offlineReceiptSignatureService;
    }

    public bool IsMraReachable => _connectivity.IsMraReachable;

    /// <summary>
    /// Returns false when sync must wait for connectivity (without consuming queue retries).
    /// </summary>
    public bool CanUploadNow()
    {
        if (!_options.RequireMraConnectivity)
        {
            return true;
        }

        return _connectivity.IsMraReachable;
    }

    /// <summary>
    /// Drains eligible PENDING offline transactions while MRA is reachable.
    /// </summary>
    public async Task<OfflineSyncDrainResult> DrainPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUploadNow())
        {
            _logger.LogDebug("Offline sync paused — MRA is not reachable.");
            return OfflineSyncDrainResult.PausedConnectivity();
        }

        var processed = 0;
        var quarantined = 0;
        var submitted = 0;
        var maxBatch = _options.MaxDrainBatchSize <= 0 ? int.MaxValue : _options.MaxDrainBatchSize;

        while (processed < maxBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanUploadNow())
            {
                _logger.LogInformation(
                    "Offline sync interrupted after {Processed} item(s) — MRA connectivity lost.",
                    processed);
                break;
            }

            var outcome = await ProcessNextCompliantAsync(cancellationToken).ConfigureAwait(false);
            if (outcome is null)
            {
                break;
            }

            processed++;
            if (outcome.IsQuarantined)
            {
                quarantined++;
            }
            else if (outcome.SubmittedOnline)
            {
                submitted++;
            }
        }

        if (processed > 0)
        {
            _logger.LogInformation(
                "Offline sync drained {Processed} item(s) (submitted={Submitted}, quarantined={Quarantined}).",
                processed,
                submitted,
                quarantined);
        }

        return new OfflineSyncDrainResult
        {
            ProcessedCount = processed,
            SubmittedCount = submitted,
            QuarantinedCount = quarantined,
            ConnectivityPaused = !CanUploadNow() && processed == 0
        };
    }

    /// <summary>
    /// Loads the next FIFO item, ensures <c>offlineSignature</c>, validates age, then uploads.
    /// </summary>
    public async Task<SaleQueueResult?> ProcessNextCompliantAsync(CancellationToken cancellationToken = default)
    {
        if (!CanUploadNow())
        {
            return null;
        }

        // Delegate claim/load/submit to the queue service after injecting pre-submit compliance hook.
        return await _queueService
            .ProcessNextFifoWithComplianceAsync(
                PrepareAndValidateForUploadAsync,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures HMAC offlineSignature is present and age limits are respected before EIS upload.
    /// Server-side crypto validation uses the shared terminal secret; the POS must send the matching signature.
    /// </summary>
    public async Task<OfflineUploadPreparationResult> PrepareAndValidateForUploadAsync(
        SubmitSalesTransactionRequest request,
        DateTime queuedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prepared = request;
        if (_options.RequireOfflineSignature
            || string.IsNullOrWhiteSpace(prepared.InvoiceSummary.OfflineSignature))
        {
            if (_offlineReceiptSignatureService is not null)
            {
                prepared = await _offlineReceiptSignatureService
                    .AttachOfflineSignatureAsync(prepared, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (_options.RequireOfflineSignature
            && string.IsNullOrWhiteSpace(prepared.InvoiceSummary.OfflineSignature))
        {
            return OfflineUploadPreparationResult.Reject(
                prepared,
                "Missing offlineSignature after signing attempt. Cannot upload offline sale to MRA.");
        }

        if (!_options.EnforceTransactionAge)
        {
            return OfflineUploadPreparationResult.Accept(prepared);
        }

        var offlineLimit = await LoadOfflineLimitAsync(cancellationToken).ConfigureAwait(false);
        if (offlineLimit is null)
        {
            offlineLimit = new OfflineLimitDto
            {
                MaxTransactionAgeInHours = Math.Max(1, _options.DefaultMaxTransactionAgeInHours)
            };
        }
        else if (offlineLimit.MaxTransactionAgeInHours <= 0)
        {
            offlineLimit = new OfflineLimitDto
            {
                MaxTransactionAgeInHours = Math.Max(1, _options.DefaultMaxTransactionAgeInHours),
                MaxCummulativeAmount = offlineLimit.MaxCummulativeAmount
            };
        }

        var compliance = _complianceValidator.ValidateForUpload(
            prepared,
            offlineLimit,
            queuedAtUtc,
            pendingOfflineCumulativeAmount: 0m);
        if (!compliance.IsCompliant)
        {
            return OfflineUploadPreparationResult.Reject(
                prepared,
                compliance.Remark ?? "Offline transaction failed compliance validation.");
        }

        return OfflineUploadPreparationResult.Accept(prepared, compliance);
    }

    private async Task<OfflineLimitDto?> LoadOfflineLimitAsync(CancellationToken cancellationToken)
    {
        if (_configurationRepository is null)
        {
            return null;
        }

        var json = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.TerminalConfiguration, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var terminal = System.Text.Json.JsonSerializer.Deserialize<TerminalConfigurationDto>(
                json,
                MraJson.SerializerOptions);
            return terminal?.OfflineLimit;
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "Corrupt terminal configuration while loading offlineLimit.");
            return null;
        }
    }
}

public sealed class OfflineSyncDrainResult
{
    public int ProcessedCount { get; init; }
    public int SubmittedCount { get; init; }
    public int QuarantinedCount { get; init; }
    public bool ConnectivityPaused { get; init; }

    public static OfflineSyncDrainResult PausedConnectivity() =>
        new() { ConnectivityPaused = true };
}

public sealed class OfflineUploadPreparationResult
{
    public bool Accepted { get; init; }
    public required SubmitSalesTransactionRequest Request { get; init; }
    public string? RejectionRemark { get; init; }
    public OfflineTransactionComplianceResult? Compliance { get; init; }

    public static OfflineUploadPreparationResult Accept(
        SubmitSalesTransactionRequest request,
        OfflineTransactionComplianceResult? compliance = null) =>
        new()
        {
            Accepted = true,
            Request = request,
            Compliance = compliance
        };

    public static OfflineUploadPreparationResult Reject(
        SubmitSalesTransactionRequest request,
        string remark) =>
        new()
        {
            Accepted = false,
            Request = request,
            RejectionRemark = remark
        };
}
