using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Mra.Billing;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Mra.Services;

public interface ILastSubmittedOnlineTransactionResponseService
{
    /// <summary>Deserializes a raw EIS JSON body into the typed last-submitted-online envelope.</summary>
    LastSubmittedOnlineParseResult ParseJson(string? json);

    /// <summary>Validates an already-deserialized EIS envelope.</summary>
    LastSubmittedOnlineParseResult Validate(EisApiResponse<SubmittedTransactionData>? response);

    /// <summary>
    /// Verifies that <paramref name="data"/>'s composite <c>invoiceNumber</c> matches expected
    /// taxpayer / terminal / sequence continuity for online reconciliation.
    /// </summary>
    InvoiceSequenceCheck CheckSequenceContinuity(
        SubmittedTransactionData data,
        string? expectedSellerTin = null,
        int? expectedTerminalPosition = null,
        long? localDailySequenceFloor = null,
        IReadOnlyCollection<string>? pendingLocalInvoiceNumbers = null);

    /// <summary>
    /// Parses, validates, and reconciles the last online submission against local terminal state
    /// so Albert Retail Terminal can audit sync status vs the MRA server.
    /// </summary>
    OnlineSubmissionReconciliationResult ReconcileOnlineSubmission(
        string? rawJson,
        string? expectedSellerTin = null,
        int? expectedTerminalPosition = null,
        string? lastLocalOnlineInvoiceNumber = null,
        long? localDailySequenceFloor = null);
}

/// <summary>
/// Parses and validates MRA EIS <c>last-submitted-online-transaction</c> responses for
/// Albert Retail Terminal online sequence and audit reconciliation.
/// </summary>
public sealed class LastSubmittedOnlineTransactionResponseService
    : ILastSubmittedOnlineTransactionResponseService
{
    private readonly ILogger<LastSubmittedOnlineTransactionResponseService> _logger;

    public LastSubmittedOnlineTransactionResponseService(
        ILogger<LastSubmittedOnlineTransactionResponseService> logger)
    {
        _logger = logger;
    }

    public LastSubmittedOnlineParseResult ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return LastSubmittedOnlineParseResult.Failed("Empty MRA response body.");
        }

        try
        {
            var response = JsonSerializer.Deserialize<LastSubmittedOnlineTransactionResponse>(
                json,
                MraJson.SerializerOptions);
            return Validate(response);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize last-submitted-online-transaction JSON.");
            return LastSubmittedOnlineParseResult.Failed(
                "MRA last-submitted-online-transaction response was not valid JSON.",
                ex.Message);
        }
    }

    public LastSubmittedOnlineParseResult Validate(EisApiResponse<SubmittedTransactionData>? response)
    {
        if (response is null)
        {
            return LastSubmittedOnlineParseResult.Failed("MRA response deserialized to null.");
        }

        if (!response.IsSuccess)
        {
            var errorDetail = FormatErrors(response.Errors);
            _logger.LogWarning(
                "last-submitted-online-transaction logical failure. statusCode={StatusCode} remark={Remark} errors={Errors}",
                response.StatusCode,
                response.Remark ?? "(null)",
                errorDetail);
            return LastSubmittedOnlineParseResult.Failed(
                response.Remark ?? $"MRA returned statusCode {response.StatusCode}.",
                errorDetail,
                response.StatusCode,
                response.Errors);
        }

        if (response.Data is null)
        {
            return LastSubmittedOnlineParseResult.Failed(
                "MRA success response contained no data payload.",
                statusCode: response.StatusCode);
        }

        var header = response.Data.InvoiceHeader;
        var invoiceNumber = response.Data.ResolveInvoiceNumber();
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return LastSubmittedOnlineParseResult.Failed(
                "MRA data.invoiceHeader.invoiceNumber is missing.",
                statusCode: response.StatusCode,
                data: response.Data);
        }

        if (header is null)
        {
            return LastSubmittedOnlineParseResult.Failed(
                "MRA data.invoiceHeader is missing.",
                statusCode: response.StatusCode,
                data: response.Data);
        }

        if (response.Data.InvoiceSummary is null)
        {
            return LastSubmittedOnlineParseResult.Failed(
                "MRA data.invoiceSummary is missing.",
                statusCode: response.StatusCode,
                data: response.Data);
        }

        var hasComposite = response.Data.HasCompositeInvoiceNumber();
        response.Data.TryGetSequenceParts(out var parts);
        var lineCount = response.Data.InvoiceLineItems?.Count ?? 0;

        _logger.LogInformation(
            "Parsed last online transaction invoice={Invoice} composite={Composite} count={Count} lines={Lines} total={Total} vat={Vat} submitted={Submitted}",
            invoiceNumber,
            hasComposite,
            hasComposite ? parts.TransactionCount : null,
            lineCount,
            response.Data.InvoiceSummary.InvoiceTotal,
            response.Data.InvoiceSummary.TotalVat,
            response.Data.DateSubmitted);

        return LastSubmittedOnlineParseResult.Succeeded(
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

    public OnlineSubmissionReconciliationResult ReconcileOnlineSubmission(
        string? rawJson,
        string? expectedSellerTin = null,
        int? expectedTerminalPosition = null,
        string? lastLocalOnlineInvoiceNumber = null,
        long? localDailySequenceFloor = null)
    {
        var parsed = ParseJson(rawJson);
        if (!parsed.Success || parsed.Data is null)
        {
            return OnlineSubmissionReconciliationResult.Failed(
                parsed.Remark ?? "Unable to parse last online transaction.",
                parsed.ErrorDetail,
                parsed.StatusCode,
                parsed.Errors);
        }

        // Only enforce encoded-TIN match when the caller supplies an expected seller TIN.
        // Sandbox samples may return a sellerTIN that does not match the composite encoding.
        var sequence = CheckSequenceContinuity(
            parsed.Data,
            expectedSellerTin: expectedSellerTin,
            expectedTerminalPosition: expectedTerminalPosition,
            localDailySequenceFloor: localDailySequenceFloor);

        var audit = OnlineSubmissionAuditSnapshot.From(parsed.Data);

        if (!sequence.IsValid)
        {
            _logger.LogWarning(
                "Online submission sequence mismatch for {Invoice}: {Message}",
                sequence.InvoiceNumber,
                sequence.Message);
            return OnlineSubmissionReconciliationResult.SequenceMismatch(parsed, sequence, audit);
        }

        var localRelation = ResolveLocalRelation(
            sequence,
            lastLocalOnlineInvoiceNumber,
            localDailySequenceFloor);

        _logger.LogInformation(
            "Online submission reconciled invoice={Invoice} relation={Relation} remoteCount={Count}",
            sequence.InvoiceNumber,
            localRelation,
            sequence.TransactionCount);

        return OnlineSubmissionReconciliationResult.Reconciled(parsed, sequence, audit, localRelation);
    }

    private static OnlineLocalRelation ResolveLocalRelation(
        InvoiceSequenceCheck remote,
        string? lastLocalOnlineInvoiceNumber,
        long? localDailySequenceFloor)
    {
        if (!string.IsNullOrWhiteSpace(lastLocalOnlineInvoiceNumber)
            && MraInvoiceNumberGenerator.TryParseComposite(lastLocalOnlineInvoiceNumber, out var local)
            && remote.TaxpayerId == local.TaxpayerId
            && remote.TerminalPosition == local.TerminalPosition
            && remote.JulianDate == local.JulianDate
            && remote.TransactionCount is long remoteCount)
        {
            if (local.TransactionCount == remoteCount)
            {
                return OnlineLocalRelation.Aligned;
            }

            if (local.TransactionCount < remoteCount)
            {
                return OnlineLocalRelation.LocalBehindRemote;
            }

            return OnlineLocalRelation.LocalAheadOfRemote;
        }

        if (localDailySequenceFloor is long floor && remote.TransactionCount is long count)
        {
            if (floor == count)
            {
                return OnlineLocalRelation.Aligned;
            }

            if (floor < count)
            {
                return OnlineLocalRelation.LocalBehindRemote;
            }

            return OnlineLocalRelation.LocalAheadOfRemote;
        }

        return OnlineLocalRelation.RemoteBaselineOnly;
    }

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

public sealed class LastSubmittedOnlineParseResult
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

    public static LastSubmittedOnlineParseResult Succeeded(
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

    public static LastSubmittedOnlineParseResult Failed(
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

public enum OnlineLocalRelation
{
    RemoteBaselineOnly = 0,
    Aligned = 1,
    LocalBehindRemote = 2,
    LocalAheadOfRemote = 3
}

/// <summary>
/// Flattened audit fields extracted from a last-submitted-online <c>data</c> payload.
/// </summary>
public sealed class OnlineSubmissionAuditSnapshot
{
    public string? InvoiceNumber { get; init; }
    public DateTime? InvoiceDateTime { get; init; }
    public DateTime? DateSubmitted { get; init; }
    public string? SellerTin { get; init; }
    public string? BuyerTin { get; init; }
    public int GlobalConfigVersion { get; init; }
    public int TaxpayerConfigVersion { get; init; }
    public int TerminalConfigVersion { get; init; }
    public int LineItemCount { get; init; }
    public decimal InvoiceTotal { get; init; }
    public decimal TotalVat { get; init; }
    public string? OfflineSignature { get; init; }
    public IReadOnlyList<string> ProductCodes { get; init; } = Array.Empty<string>();

    public static OnlineSubmissionAuditSnapshot From(SubmittedTransactionData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var header = data.InvoiceHeader;
        var summary = data.InvoiceSummary;
        var lines = data.InvoiceLineItems ?? new List<SubmittedInvoiceLineItem>();

        return new OnlineSubmissionAuditSnapshot
        {
            InvoiceNumber = data.ResolveInvoiceNumber(),
            InvoiceDateTime = header?.InvoiceDateTime,
            DateSubmitted = data.DateSubmitted,
            SellerTin = header?.SellerTin,
            BuyerTin = header?.BuyerTin,
            GlobalConfigVersion = header?.GlobalConfigVersion ?? 0,
            TaxpayerConfigVersion = header?.TaxpayerConfigVersion ?? 0,
            TerminalConfigVersion = header?.TerminalConfigVersion ?? 0,
            LineItemCount = lines.Count,
            InvoiceTotal = summary?.InvoiceTotal ?? 0m,
            TotalVat = summary?.TotalVat ?? 0m,
            OfflineSignature = summary?.OfflineSignature,
            ProductCodes = lines
                .Select(l => l.ProductCode)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Cast<string>()
                .ToArray()
        };
    }
}

public sealed class OnlineSubmissionReconciliationResult
{
    public bool Success { get; init; }
    public bool SequenceValid { get; init; }
    public string? Remark { get; init; }
    public string? ErrorDetail { get; init; }
    public int StatusCode { get; init; }
    public IReadOnlyList<EisApiError>? Errors { get; init; }
    public LastSubmittedOnlineParseResult? Parse { get; init; }
    public InvoiceSequenceCheck? Sequence { get; init; }
    public OnlineSubmissionAuditSnapshot? Audit { get; init; }
    public OnlineLocalRelation LocalRelation { get; init; }

    public static OnlineSubmissionReconciliationResult Failed(
        string remark,
        string? errorDetail = null,
        int statusCode = 0,
        IReadOnlyList<EisApiError>? errors = null) =>
        new()
        {
            Success = false,
            SequenceValid = false,
            Remark = remark,
            ErrorDetail = errorDetail,
            StatusCode = statusCode,
            Errors = errors,
            LocalRelation = OnlineLocalRelation.RemoteBaselineOnly
        };

    public static OnlineSubmissionReconciliationResult SequenceMismatch(
        LastSubmittedOnlineParseResult parse,
        InvoiceSequenceCheck sequence,
        OnlineSubmissionAuditSnapshot audit) =>
        new()
        {
            Success = false,
            SequenceValid = false,
            Remark = sequence.Message,
            StatusCode = parse.StatusCode,
            Parse = parse,
            Sequence = sequence,
            Audit = audit,
            LocalRelation = OnlineLocalRelation.RemoteBaselineOnly
        };

    public static OnlineSubmissionReconciliationResult Reconciled(
        LastSubmittedOnlineParseResult parse,
        InvoiceSequenceCheck sequence,
        OnlineSubmissionAuditSnapshot audit,
        OnlineLocalRelation localRelation) =>
        new()
        {
            Success = true,
            SequenceValid = sequence.IsValid,
            Remark = parse.Remark,
            StatusCode = parse.StatusCode,
            Parse = parse,
            Sequence = sequence,
            Audit = audit,
            LocalRelation = localRelation
        };
}
