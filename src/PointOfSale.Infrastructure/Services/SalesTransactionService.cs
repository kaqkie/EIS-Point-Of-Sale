using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Billing;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;
using PointOfSale.Mra.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace PointOfSale.Infrastructure.Services;

public sealed class SalesTransactionService
{
    private readonly MraApiClient _apiClient;
    private readonly IMraTerminalAuthProvider _authProvider;
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly StockManagementService _stockManagementService;
    private readonly ILastSubmittedOfflineTransactionResponseService _lastOfflineParser;
    private readonly ILastSubmittedOnlineTransactionResponseService _lastOnlineParser;
    private readonly ILogger<SalesTransactionService> _logger;

    public SalesTransactionService(
        MraApiClient apiClient,
        IMraTerminalAuthProvider authProvider,
        ILocalInventoryRepository inventoryRepository,
        StockManagementService stockManagementService,
        ILogger<SalesTransactionService> logger,
        ILastSubmittedOfflineTransactionResponseService? lastOfflineParser = null,
        ILastSubmittedOnlineTransactionResponseService? lastOnlineParser = null)
    {
        _apiClient = apiClient;
        _authProvider = authProvider;
        _inventoryRepository = inventoryRepository;
        _stockManagementService = stockManagementService;
        _logger = logger;
        _lastOfflineParser = lastOfflineParser
            ?? new LastSubmittedOfflineTransactionResponseService(
                NullLogger<LastSubmittedOfflineTransactionResponseService>.Instance);
        _lastOnlineParser = lastOnlineParser
            ?? new LastSubmittedOnlineTransactionResponseService(
                NullLogger<LastSubmittedOnlineTransactionResponseService>.Instance);
    }

    public Task<SalesResult<SubmitSalesTransactionResponseData>> SubmitSalesTransactionAsync(
        SubmitSalesTransactionRequest request,
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<SubmitSalesTransactionRequest, SubmitSalesTransactionResponseData>(
            "sales/submit-sales-transaction",
            request,
            cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/sales/last-submitted-online-transaction</c> —
    /// empty body, <c>Accept: text/plain</c>, Authorization = raw JWT (no Bearer prefix), no x-signature.
    /// Used to verify online invoice sequence integrity against the MRA server.
    /// </summary>
    public Task<SalesResult<SubmittedTransactionData>> GetLastSubmittedOnlineTransactionAsync(
        CancellationToken cancellationToken = default) =>
        GetLastSubmittedTransactionAsync(
            "sales/last-submitted-online-transaction",
            channelLabel: "online",
            cancellationToken);

    /// <summary>
    /// <c>POST /api/v1/sales/last-submitted-offline-transaction</c> —
    /// empty body, <c>Accept: text/plain</c>, Authorization = raw JWT (no Bearer prefix), no x-signature.
    /// Used to verify offline invoice sequence continuity before syncing queued sales.
    /// </summary>
    public Task<SalesResult<SubmittedTransactionData>> GetLastSubmittedOfflineTransactionAsync(
        CancellationToken cancellationToken = default) =>
        GetLastSubmittedTransactionAsync(
            "sales/last-submitted-offline-transaction",
            channelLabel: "offline",
            cancellationToken);

    private async Task<SalesResult<SubmittedTransactionData>> GetLastSubmittedTransactionAsync(
        string relativePath,
        string channelLabel,
        CancellationToken cancellationToken)
    {
        var context = await _authProvider.GetJwtContextAsync(cancellationToken).ConfigureAwait(false);
        var response = await _apiClient
            .PostEmptyAsync<SubmittedTransactionData>(
                relativePath,
                new MraRequestContext
                {
                    JwtToken = context.JwtToken,
                    UseBearerAuthorization = context.UseBearerAuthorization,
                    AcceptHeader = "text/plain"
                },
                cancellationToken)
            .ConfigureAwait(false);

        var result = ToResult(response);
        if (!result.Success)
        {
            var errorsJson = result.Errors is null
                ? "(no errors array)"
                : JsonSerializer.Serialize(result.Errors, MraJson.SerializerOptions);
            _logger.LogWarning(
                "MRA EIS last-submitted-{Channel}-transaction returned success=false. Remark={Remark}. Errors={Errors}. statusCode={StatusCode}",
                channelLabel,
                result.Remark ?? "(null)",
                errorsJson,
                response.StatusCode);
        }
        else
        {
            _logger.LogInformation(
                "Last submitted {Channel} transaction: invoice={Invoice} submitted={SubmittedUtc}",
                channelLabel,
                result.Data?.InvoiceHeader?.InvoiceNumber ?? "(null)",
                result.Data?.DateSubmitted);
        }

        return result;
    }

    /// <summary>
    /// Queries MRA for the last online fiscal invoice and validates composite sequence integrity
    /// for Albert Retail Terminal sync-status checks.
    /// </summary>
    public Task<OfflineSequenceContinuityResult> VerifyOnlineSequenceContinuityAsync(
        CancellationToken cancellationToken = default) =>
        VerifySequenceContinuityAsync(
            GetLastSubmittedOnlineTransactionAsync,
            channelLabel: "online",
            cancellationToken);

    /// <summary>
    /// Queries MRA for the last offline fiscal invoice and raises the local daily sequence floor
    /// when the remote counter is ahead — preventing duplicate transaction counts on sync.
    /// </summary>
    public Task<OfflineSequenceContinuityResult> VerifyOfflineSequenceContinuityAsync(
        CancellationToken cancellationToken = default) =>
        VerifySequenceContinuityAsync(
            GetLastSubmittedOfflineTransactionAsync,
            channelLabel: "offline",
            cancellationToken);

    private async Task<OfflineSequenceContinuityResult> VerifySequenceContinuityAsync(
        Func<CancellationToken, Task<SalesResult<SubmittedTransactionData>>> lookupFactory,
        string channelLabel,
        CancellationToken cancellationToken)
    {
        SalesResult<SubmittedTransactionData> lookup;
        try
        {
            lookup = await lookupFactory(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to query last-submitted-{Channel}-transaction for sequence check.", channelLabel);
            return OfflineSequenceContinuityResult.Unavailable(ex.Message);
        }

        if (!lookup.Success || lookup.Data is null)
        {
            return OfflineSequenceContinuityResult.NoRemoteBaseline(lookup.Remark);
        }

        var envelope = new EisApiResponse<SubmittedTransactionData>
        {
            StatusCode = 1,
            Remark = lookup.Remark,
            Data = lookup.Data
        };

        bool hasComposite;
        SubmittedTransactionData? parsedData;
        string? parseRemark;
        InvoiceSequenceCheck sequence;

        if (string.Equals(channelLabel, "online", StringComparison.Ordinal))
        {
            var parse = _lastOnlineParser.Validate(envelope);
            hasComposite = parse.HasCompositeInvoiceNumber;
            parsedData = parse.Data;
            parseRemark = parse.Remark;
            if (!parse.Success || parsedData is null)
            {
                return OfflineSequenceContinuityResult.Unparseable(
                    lookup.Data.InvoiceHeader?.InvoiceNumber,
                    parseRemark);
            }

            sequence = _lastOnlineParser.CheckSequenceContinuity(
                parsedData,
                expectedSellerTin: parsedData.InvoiceHeader?.SellerTin);
        }
        else
        {
            var parse = _lastOfflineParser.Validate(envelope);
            hasComposite = parse.HasCompositeInvoiceNumber;
            parsedData = parse.Data;
            parseRemark = parse.Remark;
            if (!parse.Success || parsedData is null)
            {
                return OfflineSequenceContinuityResult.Unparseable(
                    lookup.Data.InvoiceHeader?.InvoiceNumber,
                    parseRemark);
            }

            sequence = _lastOfflineParser.CheckSequenceContinuity(
                parsedData,
                expectedSellerTin: parsedData.InvoiceHeader?.SellerTin);
        }

        if (!sequence.IsValid || !hasComposite)
        {
            _logger.LogWarning(
                "Last {Channel} invoice sequence check failed for {Invoice}: {Message}",
                channelLabel,
                sequence.InvoiceNumber,
                sequence.Message);
            return OfflineSequenceContinuityResult.Unparseable(sequence.InvoiceNumber, sequence.Message);
        }

        return OfflineSequenceContinuityResult.Aligned(
            sequence.InvoiceNumber!,
            sequence.TaxpayerId!.Value,
            sequence.TerminalPosition!.Value,
            sequence.JulianDate!.Value,
            sequence.TransactionCount!.Value,
            parsedData.DateSubmitted,
            lookup.Remark);
    }

    public Task<SalesResult<InvoiceLookupResponseData>> GetInvoiceByNumberAsync(
        InvoiceNumberQueryRequest request,
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<InvoiceNumberQueryRequest, InvoiceLookupResponseData>(
            "sales/get-invoice-by-number",
            request,
            cancellationToken);

    public Task<SalesResult<ProcessCreditDebitNoteResponseData>> ProcessCreditDebitNoteAsync(
        ProcessCreditDebitNoteRequest request,
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<ProcessCreditDebitNoteRequest, ProcessCreditDebitNoteResponseData>(
            "sales/process-credit-debit-note",
            request,
            cancellationToken);

    public Task<SalesResult<CancelReceiptResponseData>> CancelReceiptAsync(
        CancelReceiptRequest request,
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<CancelReceiptRequest, CancelReceiptResponseData>(
            "sales/cancel-receipt",
            request,
            cancellationToken);

    public Task<SalesResult<GetVoidReceiptsResponseData>> GetVoidReceiptsAsync(
        GetVoidReceiptsRequest request,
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<GetVoidReceiptsRequest, GetVoidReceiptsResponseData>(
            "sales/get-void-receipts",
            request,
            cancellationToken);

    public async Task ValidateSaleAgainstInventoryAsync(
        SubmitSalesTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequestStructure(request);

        _ = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);

        foreach (var line in request.InvoiceLineItems.Where(x => x.IsProduct))
        {
            var local = await _inventoryRepository
                .GetByProductCodeAsync(line.ProductCode.Trim(), cancellationToken)
                .ConfigureAwait(false);

            if (local is null)
            {
                await _stockManagementService
                    .GetWarehouseInventoryAsync(
                        new Mra.Contracts.Stock.WarehouseInventoryRequest { Page = 1, PageSize = 200 },
                        cancellationToken)
                    .ConfigureAwait(false);

                local = await _inventoryRepository
                    .GetByProductCodeAsync(line.ProductCode.Trim(), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (local is null)
            {
                throw new InvalidOperationException(
                    $"Product '{line.ProductCode}' is not in the local inventory cache. " +
                    "Synchronize warehouse stock via StockManagementService.GetWarehouseInventoryAsync before selling.");
            }

            if (local.StockQuantity < line.Quantity)
            {
                throw new InvalidOperationException(
                    $"Insufficient stock for '{line.ProductCode}'. Available {local.StockQuantity}, requested {line.Quantity}.");
            }

            if (!string.IsNullOrWhiteSpace(local.TaxRateId) &&
                !local.TaxRateId.Equals(line.TaxRateId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Tax rate mismatch for '{line.ProductCode}'. Local={local.TaxRateId}, sale={line.TaxRateId}.");
            }
        }
    }

    public SubmitSalesTransactionRequest PreparePayload(SubmitSalesTransactionRequest request, bool forceOffline) =>
        request;

    /// <summary>
    /// Legacy helper: HMAC-SHA512 over arbitrary payload JSON (EIS message-hash style).
    /// Prefer <see cref="ComputeOfflineReceiptSignatureAsync"/> for offline sales compliance.
    /// </summary>
    public async Task<string> ComputeOfflineSignatureAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(context.SecretKey))
        {
            throw new InvalidOperationException("Secret key unavailable for offline signature.");
        }

        return HmacSignatureService.ComputeHmacSha512Base64(payloadJson, context.SecretKey);
    }

    /// <summary>
    /// MRA offline receipt signing: Julian/Base64 <c>TI</c>, parameter string <c>TI/N/I/V/T</c>,
    /// HMAC-SHA256 <c>offlineDataSignature</c>, and full <c>ValidationURL</c>.
    /// </summary>
    public Task<MraOfflineReceiptSignatureResult> ComputeOfflineReceiptSignatureAsync(
        SubmitSalesTransactionRequest request,
        string? offlineValidationBaseUrl = null,
        CancellationToken cancellationToken = default) =>
        ComputeOfflineReceiptSignatureCoreAsync(request, offlineValidationBaseUrl, cancellationToken);

    private async Task<MraOfflineReceiptSignatureResult> ComputeOfflineReceiptSignatureCoreAsync(
        SubmitSalesTransactionRequest request,
        string? offlineValidationBaseUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(context.SecretKey))
        {
            throw new InvalidOperationException("Secret key unavailable for offline ValidationURL signature.");
        }

        return MraOfflineReceiptSigning.GenerateFromSalesRequest(
            request,
            context.SecretKey,
            offlineValidationBaseUrl);
    }

    public async Task ApplyLocalInventoryDeductionsAsync(
        SubmitSalesTransactionRequest request,
        CancellationToken cancellationToken = default)
    {
        foreach (var line in request.InvoiceLineItems.Where(x => x.IsProduct))
        {
            var local = await _inventoryRepository
                .GetByProductCodeAsync(line.ProductCode.Trim(), cancellationToken)
                .ConfigureAwait(false);

            if (local is null)
            {
                continue;
            }

            local.StockQuantity = Math.Max(0, local.StockQuantity - line.Quantity);
            await _inventoryRepository.UpsertAsync(local, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SalesResult<TResponse>> PostSignedAsync<TRequest, TResponse>(
        string relativePath,
        TRequest body,
        CancellationToken cancellationToken)
    {
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);

        // Align sales payloads with OpenAPI invoiceHeader / line / summary expectations.
        // Preserve activated taxRateId values (e.g. "A") — do not invent non-MRA ids.
        object payloadToSend = body is SubmitSalesTransactionRequest sales
            ? MraFiscalPayloadNormalizer.Normalize(sales)
            : body!;

        var response = await _apiClient
            .PostAsync<object, TResponse>(relativePath, payloadToSend, context, cancellationToken)
            .ConfigureAwait(false);

        var result = ToResult(response);
        if (!result.Success)
        {
            var errorsJson = result.Errors is null
                ? "(no errors array)"
                : JsonSerializer.Serialize(result.Errors, MraJson.SerializerOptions);
            _logger.LogWarning(
                "MRA EIS {Path} returned success=false. Remark={Remark}. Errors={Errors}. statusCode={StatusCode}",
                relativePath,
                result.Remark ?? "(null)",
                errorsJson,
                response.StatusCode);
        }

        return result;
    }

    private static void ValidateRequestStructure(SubmitSalesTransactionRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvoiceHeader.InvoiceNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvoiceHeader.SellerTin);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvoiceHeader.SiteId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvoiceHeader.PaymentMethod);

        if (request.InvoiceLineItems.Count == 0)
        {
            throw new InvalidOperationException("At least one invoice line item is required.");
        }

        if (request.InvoiceSummary.TaxBreakDown.Count == 0)
        {
            throw new InvalidOperationException("Invoice tax breakdown is required.");
        }
    }

    private static SalesResult<T> ToResult<T>(EisApiResponse<T> response) =>
        response.IsSuccess && response.Data is not null
            ? SalesResult<T>.Succeeded(response.Data, response.Remark)
            : SalesResult<T>.Failed(response.StatusCode, response.Remark, response.Errors, response.Data);
}

public sealed class SalesResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public int StatusCode { get; init; }
    public string? Remark { get; init; }
    public IReadOnlyList<EisApiError>? Errors { get; init; }

    public static SalesResult<T> Succeeded(T data, string? remark) =>
        new() { Success = true, Data = data, StatusCode = 1, Remark = remark };

    public static SalesResult<T> Failed(string? remark, IReadOnlyList<EisApiError>? errors) =>
        Failed(statusCode: 0, remark, errors);

    public static SalesResult<T> Failed(
        int statusCode,
        string? remark,
        IReadOnlyList<EisApiError>? errors,
        T? data = default) =>
        new() { Success = false, StatusCode = statusCode, Remark = remark, Errors = errors, Data = data };
}

/// <summary>
/// Outcome of comparing the MRA last-submitted-offline invoice against local daily sequence state.
/// </summary>
public sealed class OfflineSequenceContinuityResult
{
    public bool RemoteBaselineAvailable { get; init; }
    public bool Parsed { get; init; }
    public string? LastInvoiceNumber { get; init; }
    public long? LastTransactionCount { get; init; }
    public long? TaxpayerId { get; init; }
    public int? TerminalPosition { get; init; }
    public int? JulianDate { get; init; }
    public DateTime? DateSubmittedUtc { get; init; }
    public string? Remark { get; init; }

    public static OfflineSequenceContinuityResult Unavailable(string? remark) =>
        new() { Remark = remark };

    public static OfflineSequenceContinuityResult NoRemoteBaseline(string? remark) =>
        new() { Remark = remark ?? "No last offline transaction returned by MRA." };

    public static OfflineSequenceContinuityResult Unparseable(string? invoiceNumber, string? remark) =>
        new()
        {
            RemoteBaselineAvailable = true,
            LastInvoiceNumber = invoiceNumber,
            Remark = remark
        };

    public static OfflineSequenceContinuityResult Aligned(
        string invoiceNumber,
        long taxpayerId,
        int terminalPosition,
        int julianDate,
        long transactionCount,
        DateTime? dateSubmitted,
        string? remark) =>
        new()
        {
            RemoteBaselineAvailable = true,
            Parsed = true,
            LastInvoiceNumber = invoiceNumber,
            TaxpayerId = taxpayerId,
            TerminalPosition = terminalPosition,
            JulianDate = julianDate,
            LastTransactionCount = transactionCount,
            DateSubmittedUtc = dateSubmitted,
            Remark = remark
        };
}
