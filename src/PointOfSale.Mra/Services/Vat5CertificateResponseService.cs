using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Mra.Services;

public interface IVat5CertificateResponseService
{
    /// <summary>Deserializes a raw EIS JSON body into the typed VAT5 validation envelope.</summary>
    Vat5CertificateParseResult ParseJson(string? json, decimal requestedQuantity = 0m, decimal alreadyConsumedQuantity = 0m);

    /// <summary>Validates an already-deserialized EIS envelope and evaluates relief eligibility.</summary>
    Vat5CertificateParseResult Validate(
        EisApiResponse<Vat5CertificateValidationData>? response,
        decimal requestedQuantity = 0m,
        decimal alreadyConsumedQuantity = 0m,
        DateTime? asOfUtc = null);

    /// <summary>
    /// Processes a successful validation <c>data</c> payload: authenticity, expiry, and quantity coverage
    /// so Albert Retail Terminal can decide whether to apply VAT relief.
    /// </summary>
    Vat5CertificateEvaluation EvaluateCertificate(
        Vat5CertificateValidationData data,
        decimal requestedQuantity,
        decimal alreadyConsumedQuantity = 0m,
        DateTime? asOfUtc = null);
}

/// <summary>
/// Parses and evaluates MRA EIS <c>validate-vat5-certificate</c> responses for
/// Albert Retail Terminal relief-supply checkout.
/// </summary>
public sealed class Vat5CertificateResponseService : IVat5CertificateResponseService
{
    private readonly ILogger<Vat5CertificateResponseService> _logger;

    public Vat5CertificateResponseService(ILogger<Vat5CertificateResponseService> logger)
    {
        _logger = logger;
    }

    public Vat5CertificateParseResult ParseJson(
        string? json,
        decimal requestedQuantity = 0m,
        decimal alreadyConsumedQuantity = 0m)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Vat5CertificateParseResult.Failed("Empty MRA response body.");
        }

        try
        {
            var response = JsonSerializer.Deserialize<ValidateVat5CertificateResponse>(
                json,
                MraJson.SerializerOptions);
            return Validate(response, requestedQuantity, alreadyConsumedQuantity);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize validate-vat5-certificate JSON.");
            return Vat5CertificateParseResult.Failed(
                "MRA validate-vat5-certificate response was not valid JSON.",
                ex.Message);
        }
    }

    public Vat5CertificateParseResult Validate(
        EisApiResponse<Vat5CertificateValidationData>? response,
        decimal requestedQuantity = 0m,
        decimal alreadyConsumedQuantity = 0m,
        DateTime? asOfUtc = null)
    {
        if (response is null)
        {
            return Vat5CertificateParseResult.Failed("MRA response deserialized to null.");
        }

        if (!response.IsSuccess)
        {
            var errorDetail = FormatErrors(response.Errors);
            _logger.LogWarning(
                "validate-vat5-certificate logical failure. statusCode={StatusCode} remark={Remark} errors={Errors}",
                response.StatusCode,
                response.Remark ?? "(null)",
                errorDetail);
            return Vat5CertificateParseResult.Failed(
                response.Remark ?? $"MRA returned statusCode {response.StatusCode}.",
                errorDetail,
                response.StatusCode,
                response.Errors);
        }

        if (response.Data is null)
        {
            return Vat5CertificateParseResult.Failed(
                "MRA success response contained no VAT5 certificate data.",
                statusCode: response.StatusCode);
        }

        var evaluation = EvaluateCertificate(
            response.Data,
            requestedQuantity,
            alreadyConsumedQuantity,
            asOfUtc);

        _logger.LogInformation(
            "Parsed VAT5 certificate project={Project} cert={Certificate} approved={Approved} remaining={Remaining} expired={Expired} allowsRelief={AllowsRelief}",
            evaluation.ProjectNumber,
            evaluation.CertificateNumber,
            evaluation.ApprovedQuantity,
            evaluation.RemainingQuantity,
            evaluation.IsExpired,
            evaluation.AllowsReliefSupply);

        return Vat5CertificateParseResult.Succeeded(
            response.Data,
            evaluation,
            response.Remark,
            response.StatusCode);
    }

    public Vat5CertificateEvaluation EvaluateCertificate(
        Vat5CertificateValidationData data,
        decimal requestedQuantity,
        decimal alreadyConsumedQuantity = 0m,
        DateTime? asOfUtc = null)
    {
        ArgumentNullException.ThrowIfNull(data);

        var project = TrimOrNull(data.ProjectNumber);
        var certificate = TrimOrNull(data.ResolveCertificateNumber());
        var authentic = !string.IsNullOrWhiteSpace(project)
            && !string.IsNullOrWhiteSpace(certificate)
            && data.Quantity > 0
            && data.IsValid != false;

        var now = (asOfUtc ?? DateTime.UtcNow).ToUniversalTime();
        var expired = data.DateOfExpiry is DateTime expiry
            && expiry.ToUniversalTime().Date < now.Date;

        var approved = data.Quantity;
        var consumed = Math.Max(0m, alreadyConsumedQuantity);
        if (consumed > approved)
        {
            consumed = approved;
        }

        var remaining = Math.Max(0m, approved - consumed);
        var requested = Math.Max(0m, requestedQuantity);
        var canCover = authentic && !expired && (requested <= 0m || remaining >= requested);

        string? message;
        if (!authentic)
        {
            message = "VAT5 certificate data is incomplete (projectNumber, certificateNumber, or quantity).";
        }
        else if (expired)
        {
            message = $"VAT5 certificate expired on {data.DateOfExpiry:yyyy-MM-dd}.";
        }
        else if (requested > 0m && remaining < requested)
        {
            message = $"Insufficient VAT5 quantity remaining ({remaining}) for requested {requested}.";
        }
        else
        {
            message = "VAT5 certificate is valid for relief supply.";
        }

        return new Vat5CertificateEvaluation
        {
            IsAuthentic = authentic,
            IsExpired = expired,
            CanCoverRequestedQuantity = canCover,
            AllowsReliefSupply = authentic && !expired && canCover,
            ProjectNumber = project,
            CertificateNumber = certificate,
            ApprovedQuantity = approved,
            AlreadyConsumedQuantity = consumed,
            RemainingQuantity = remaining,
            RequestedQuantity = requested,
            DateOfIssue = data.DateOfIssue,
            DateOfExpiry = data.DateOfExpiry,
            Message = message
        };
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

public sealed class Vat5CertificateEvaluation
{
    public bool IsAuthentic { get; init; }
    public bool IsExpired { get; init; }
    public bool CanCoverRequestedQuantity { get; init; }
    public bool AllowsReliefSupply { get; init; }
    public string? ProjectNumber { get; init; }
    public string? CertificateNumber { get; init; }
    public decimal ApprovedQuantity { get; init; }
    public decimal AlreadyConsumedQuantity { get; init; }
    public decimal RemainingQuantity { get; init; }
    public decimal RequestedQuantity { get; init; }
    public DateTime? DateOfIssue { get; init; }
    public DateTime? DateOfExpiry { get; init; }
    public string? Message { get; init; }
}

public sealed class Vat5CertificateParseResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string? Remark { get; init; }
    public string? ErrorDetail { get; init; }
    public IReadOnlyList<EisApiError>? Errors { get; init; }
    public Vat5CertificateValidationData? Data { get; init; }
    public Vat5CertificateEvaluation? Evaluation { get; init; }

    public bool AllowsReliefSupply => Success && Evaluation?.AllowsReliefSupply == true;
    public bool IsExpired => Evaluation?.IsExpired == true;
    public decimal RemainingQuantity => Evaluation?.RemainingQuantity ?? 0m;

    public static Vat5CertificateParseResult Succeeded(
        Vat5CertificateValidationData data,
        Vat5CertificateEvaluation evaluation,
        string? remark,
        int statusCode) =>
        new()
        {
            Success = true,
            StatusCode = statusCode,
            Remark = remark,
            Data = data,
            Evaluation = evaluation
        };

    public static Vat5CertificateParseResult Failed(
        string remark,
        string? errorDetail = null,
        int statusCode = 0,
        IReadOnlyList<EisApiError>? errors = null) =>
        new()
        {
            Success = false,
            StatusCode = statusCode,
            Remark = remark,
            ErrorDetail = errorDetail,
            Errors = errors
        };
}
