using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Mra.Services;

public interface ILastSubmittedOfflineTransactionResponseService
{
    /// <summary>Deserializes a raw EIS JSON body into the typed last-submitted-offline envelope.</summary>
    LastSubmittedOfflineParseResult ParseJson(string? json);

    /// <summary>Validates an already-deserialized EIS envelope.</summary>
    LastSubmittedOfflineParseResult Validate(EisApiResponse<SubmittedTransactionData>? response);

    /// <summary>
    /// Verifies that <paramref name="data"/>'s <c>invoiceNumber</c> (e.g. <c>E-De-JYxh-B</c>)
    /// matches expected taxpayer / terminal / sequence continuity for offline reconciliation.
    /// </summary>
    InvoiceSequenceCheck CheckSequenceContinuity(
        SubmittedTransactionData data,
        string? expectedSellerTin = null,
        int? expectedTerminalPosition = null,
        long? localDailySequenceFloor = null,
        IReadOnlyCollection<string>? pendingLocalInvoiceNumbers = null);
}

/// <summary>
/// Parses and validates MRA EIS <c>last-submitted-offline-transaction</c> responses for
/// Albert Retail Terminal offline-queue reconciliation.
/// </summary>
public sealed class LastSubmittedOfflineTransactionResponseService
    : ILastSubmittedOfflineTransactionResponseService
{
    private readonly ILogger<LastSubmittedOfflineTransactionResponseService> _logger;

    public LastSubmittedOfflineTransactionResponseService(
        ILogger<LastSubmittedOfflineTransactionResponseService> logger)
    {
        _logger = logger;
    }

    public LastSubmittedOfflineParseResult ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return LastSubmittedOfflineParseResult.Failed("Empty MRA response body.");
        }

        try
        {
            var response = JsonSerializer.Deserialize<LastSubmittedOfflineTransactionResponse>(
                json,
                MraJson.SerializerOptions);
            return Validate(response);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize last-submitted-offline-transaction JSON.");
            return LastSubmittedOfflineParseResult.Failed(
                "MRA last-submitted-offline-transaction response was not valid JSON.",
                ex.Message);
        }
    }

    public LastSubmittedOfflineParseResult Validate(EisApiResponse<SubmittedTransactionData>? response)
    {
        if (response is null)
        {
            return LastSubmittedOfflineParseResult.Failed("MRA response deserialized to null.");
        }

        if (!response.IsSuccess)
        {
            var errorDetail = FormatErrors(response.Errors);
            _logger.LogWarning(
                "last-submitted-offline-transaction logical failure. statusCode={StatusCode} remark={Remark} errors={Errors}",
                response.StatusCode,
                response.Remark ?? "(null)",
                errorDetail);
            return LastSubmittedOfflineParseResult.Failed(
                response.Remark ?? $"MRA returned statusCode {response.StatusCode}.",
                errorDetail,
                response.StatusCode,
                response.Errors);
        }

        if (response.Data is null)
        {
            return LastSubmittedOfflineParseResult.Failed(
                "MRA success response contained no data payload.",
                statusCode: response.StatusCode);
        }

        var invoiceNumber = response.Data.ResolveInvoiceNumber();
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return LastSubmittedOfflineParseResult.Failed(
                "MRA data.invoiceHeader.invoiceNumber is missing.",
                statusCode: response.StatusCode,
                data: response.Data);
        }

        var hasComposite = response.Data.HasCompositeInvoiceNumber();
        response.Data.TryGetSequenceParts(out var parts);

        _logger.LogInformation(
            "Parsed last offline transaction invoice={Invoice} composite={Composite} count={Count} submitted={Submitted}",
            invoiceNumber,
            hasComposite,
            hasComposite ? parts.TransactionCount : null,
            response.Data.DateSubmitted);

        return LastSubmittedOfflineParseResult.Succeeded(
            response.Data,
            response.Remark,
            response.StatusCode,
            hasComposite ? parts : null);
    }

    public InvoiceSequenceCheck CheckSequenceContinuity(
        SubmittedTransactionData data,
        string? expectedSellerTin = null,
        int? expectedTerminalPosition = null,
        long? localDailySequenceFloor = null,
        IReadOnlyCollection<string>? pendingLocalInvoiceNumbers = null) =>
        LastSubmittedInvoiceSequenceValidator.Check(
            data,
            expectedSellerTin,
            expectedTerminalPosition,
            localDailySequenceFloor,
            pendingLocalInvoiceNumbers);

    private static string FormatErrors(IReadOnlyList<EisApiError>? errors)
    {
        if (errors is null || errors.Count == 0)
        {
            return "(none)";
        }

        return string.Join(
            "; ",
            errors.Take(8).Select(e =>
                string.IsNullOrWhiteSpace(e.FieldName)
                    ? $"[{e.ErrorCode}] {e.ErrorMessage}"
                    : $"[{e.ErrorCode}] {e.FieldName}: {e.ErrorMessage}"));
    }
}

public sealed class LastSubmittedOfflineParseResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string? Remark { get; init; }
    public string? ErrorDetail { get; init; }
    public SubmittedTransactionData? Data { get; init; }
    public IReadOnlyList<EisApiError>? Errors { get; init; }
    public string? InvoiceNumber => Data.ResolveInvoiceNumber();
    public bool HasCompositeInvoiceNumber { get; init; }
    public long? TransactionCount { get; init; }
    public int? JulianDate { get; init; }
    public long? TaxpayerId { get; init; }
    public int? TerminalPosition { get; init; }

    public static LastSubmittedOfflineParseResult Succeeded(
        SubmittedTransactionData data,
        string? remark,
        int statusCode,
        (long TaxpayerId, int TerminalPosition, int JulianDate, long TransactionCount)? parts) =>
        new()
        {
            Success = true,
            StatusCode = statusCode,
            Remark = remark,
            Data = data,
            HasCompositeInvoiceNumber = parts is not null,
            TaxpayerId = parts?.TaxpayerId,
            TerminalPosition = parts?.TerminalPosition,
            JulianDate = parts?.JulianDate,
            TransactionCount = parts?.TransactionCount
        };

    public static LastSubmittedOfflineParseResult Failed(
        string remark,
        string? errorDetail = null,
        int statusCode = 0,
        IReadOnlyList<EisApiError>? errors = null,
        SubmittedTransactionData? data = null) =>
        new()
        {
            Success = false,
            StatusCode = statusCode,
            Remark = remark,
            ErrorDetail = errorDetail,
            Errors = errors,
            Data = data
        };
}
