using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Infrastructure.Services;

public sealed class SalesTransactionService
{
    private readonly MraApiClient _apiClient;
    private readonly IMraTerminalAuthProvider _authProvider;
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly StockManagementService _stockManagementService;
    private readonly ILogger<SalesTransactionService> _logger;

    public SalesTransactionService(
        MraApiClient apiClient,
        IMraTerminalAuthProvider authProvider,
        ILocalInventoryRepository inventoryRepository,
        StockManagementService stockManagementService,
        ILogger<SalesTransactionService> logger)
    {
        _apiClient = apiClient;
        _authProvider = authProvider;
        _inventoryRepository = inventoryRepository;
        _stockManagementService = stockManagementService;
        _logger = logger;
    }

    public Task<SalesResult<SubmitSalesTransactionResponseData>> SubmitSalesTransactionAsync(
        SubmitSalesTransactionRequest request,
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<SubmitSalesTransactionRequest, SubmitSalesTransactionResponseData>(
            "sales/submit-sales-transaction",
            request,
            cancellationToken);

    public Task<SalesResult<SalesInvoiceSnapshotDto>> GetLastSubmittedOnlineTransactionAsync(
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<object, SalesInvoiceSnapshotDto>(
            "sales/last-submitted-online-transaction",
            new { },
            cancellationToken);

    public Task<SalesResult<SalesInvoiceSnapshotDto>> GetLastSubmittedOfflineTransactionAsync(
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<object, SalesInvoiceSnapshotDto>(
            "sales/last-submitted-offline-transaction",
            new { },
            cancellationToken);

    public Task<SalesResult<SalesInvoiceSnapshotDto>> GetInvoiceByNumberAsync(
        InvoiceNumberQueryRequest request,
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<InvoiceNumberQueryRequest, SalesInvoiceSnapshotDto>(
            "sales/get-invoice-by-number",
            request,
            cancellationToken);

    public Task<SalesResult<SubmitSalesTransactionResponseData>> ProcessCreditDebitNoteAsync(
        ProcessCreditDebitNoteRequest request,
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<ProcessCreditDebitNoteRequest, SubmitSalesTransactionResponseData>(
            "sales/process-credit-debit-note",
            request,
            cancellationToken);

    public Task<SalesResult<SubmitSalesTransactionResponseData>> CancelReceiptAsync(
        CancelReceiptRequest request,
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<CancelReceiptRequest, SubmitSalesTransactionResponseData>(
            "sales/cancel-receipt",
            request,
            cancellationToken);

    public Task<SalesResult<IReadOnlyList<VoidReceiptDto>>> GetVoidReceiptsAsync(
        GetVoidReceiptsRequest request,
        CancellationToken cancellationToken = default) =>
        PostSignedAsync<GetVoidReceiptsRequest, IReadOnlyList<VoidReceiptDto>>(
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
                        new Mra.Contracts.Stock.WarehouseInventoryRequest { PageNumber = 1, PageSize = 200 },
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

    public async Task<string> ComputeOfflineSignatureAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        var context = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(context.SecretKey))
        {
            throw new InvalidOperationException("Secret key unavailable for offline signature.");
        }

        return HmacSignatureService.ComputeHmacSha512Base64(payloadJson, context.SecretKey);
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

        // Align sales payloads with OpenAPI invoiceHeader / line / summary expectations + 17.5% VAT.
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
            : SalesResult<T>.Failed(response.Remark, response.Errors);
}

public sealed class SalesResult<T>
{
    public bool Success { get; init; }
    public T? Data { get; init; }
    public string? Remark { get; init; }
    public IReadOnlyList<EisApiError>? Errors { get; init; }

    public static SalesResult<T> Succeeded(T data, string? remark) =>
        new() { Success = true, Data = data, Remark = remark };

    public static SalesResult<T> Failed(string? remark, IReadOnlyList<EisApiError>? errors) =>
        new() { Success = false, Remark = remark, Errors = errors };
}
