using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Http;

namespace PointOfSale.Mra.Services;

public interface IMraEisResponseEvaluator
{
    /// <summary>Evaluate a deserialized EIS envelope (logical success or failure).</summary>
    MraEisResponseEvaluation Evaluate<T>(EisApiResponse<T> response);

    /// <summary>
    /// Evaluate raw status / remark / errors without a typed data payload
    /// (e.g. from queue handling or HTTP error bodies).
    /// </summary>
    MraEisResponseEvaluation Evaluate(
        int statusCode,
        string? remark,
        IReadOnlyList<EisApiError>? errors);

    /// <summary>Parse an HTTP/transport <see cref="MraApiException"/> response body when possible.</summary>
    MraEisResponseEvaluation EvaluateException(MraApiException exception);
}

/// <summary>
/// Categorizes MRA EIS <c>statusCode</c> and field <c>errorCode</c> values into
/// operator guidance and queue actions (retry, refresh JWT, sync configs, quarantine, re-activate).
/// </summary>
public sealed class MraEisResponseEvaluator : IMraEisResponseEvaluator
{
    private readonly ILogger<MraEisResponseEvaluator> _logger;

    public MraEisResponseEvaluator(ILogger<MraEisResponseEvaluator> logger)
    {
        _logger = logger;
    }

    public MraEisResponseEvaluation Evaluate<T>(EisApiResponse<T> response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccess)
        {
            return MraEisResponseEvaluation.Success(response.Remark);
        }

        return Evaluate(response.StatusCode, response.Remark, response.Errors);
    }

    public MraEisResponseEvaluation Evaluate(
        int statusCode,
        string? remark,
        IReadOnlyList<EisApiError>? errors)
    {
        var errorList = errors?.ToList() ?? [];
        var fieldCode = ResolvePrimaryFieldErrorCode(errorList);

        // Field-level validation codes take precedence when present — they describe the payload defect.
        if (fieldCode is MraEisStatusCodes.MissingMandatoryField or MraEisStatusCodes.InvalidFieldValue)
        {
            var evaluation = BuildFromFieldError(statusCode, remark, errorList, fieldCode.Value);
            LogEvaluation(evaluation);
            return evaluation;
        }

        var evaluationFromStatus = statusCode switch
        {
            MraEisStatusCodes.ServerError => Build(
                statusCode,
                remark,
                errorList,
                MraEisFailureCategory.ServerError,
                MraEisRecommendedAction.RetryLater,
                MraEisStatusCodes.ServerError,
                "MRA service unavailable",
                "The MRA EIS backend is temporarily unavailable (server or database error). " +
                "The sale can be queued offline and will retry automatically when the service recovers."),

            MraEisStatusCodes.AuthenticationFailure => Build(
                statusCode,
                remark,
                errorList,
                MraEisFailureCategory.AuthenticationFailure,
                MraEisRecommendedAction.RefreshCredentials,
                MraEisStatusCodes.AuthenticationFailure,
                "MRA authentication failed",
                "Terminal credentials were rejected by MRA. Renew the terminal JWT / secret, " +
                "then retry the sale from Queue Sync."),

            MraEisStatusCodes.BusinessRuleViolation => Build(
                statusCode,
                remark,
                errorList,
                MraEisFailureCategory.BusinessRuleViolation,
                MraEisRecommendedAction.BlockUntilReady,
                MraEisStatusCodes.BusinessRuleViolation,
                "MRA business rule blocked",
                "MRA rejected this request due to a business rule (for example, selling before terminal activation). " +
                "Complete activation and configuration sync before retrying."),

            MraEisStatusCodes.OutdatedConfiguration => Build(
                statusCode,
                remark,
                errorList,
                MraEisFailureCategory.OutdatedConfiguration,
                MraEisRecommendedAction.SyncLatestConfigs,
                MraEisStatusCodes.OutdatedConfiguration,
                "MRA configuration outdated",
                "Terminal configuration versions are out of date. Run get-latest-configs " +
                "(Terminal / Onboarding sync), then resubmit the sale."),

            MraEisStatusCodes.TerminalDeactivated => Build(
                statusCode,
                remark,
                errorList,
                MraEisFailureCategory.TerminalDeactivated,
                MraEisRecommendedAction.ReactivateTerminal,
                MraEisStatusCodes.TerminalDeactivated,
                "Terminal de-activated",
                "This terminal has been de-activated by MRA. Re-activate the terminal with a valid " +
                "activation code before submitting sales."),

            _ => BuildRemarkAwareUnknown(statusCode, remark, errorList, fieldCode)
        };

        LogEvaluation(evaluationFromStatus);
        return evaluationFromStatus;
    }

    public MraEisResponseEvaluation EvaluateException(MraApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (TryParseEnvelope(exception.ResponseBody, out var statusCode, out var remark, out var errors))
        {
            var evaluation = Evaluate(statusCode, remark, errors);
            if (evaluation.Category != MraEisFailureCategory.Unknown
                || evaluation.Errors.Count > 0
                || statusCode is not (0 or 1))
            {
                return evaluation;
            }
        }

        // Fall back to existing HTTP heuristics when the body has no logical status codes.
        if (exception.LooksLikeValidationOrClientError())
        {
            var quarantine = Build(
                statusCode: 0,
                remark: exception.Message,
                errors: [],
                MraEisFailureCategory.InvalidFieldValue,
                MraEisRecommendedAction.QuarantinePayload,
                primaryCode: 0,
                "MRA rejected this sale",
                "The invoice failed MRA validation and should be quarantined. " +
                "Check seller TIN, site id, tax rates, and required fields, then correct via Queue Sync.\n\n" +
                Truncate(exception.Message));
            LogEvaluation(quarantine);
            return quarantine;
        }

        var retry = Build(
            statusCode: 0,
            remark: exception.Message,
            errors: [],
            MraEisFailureCategory.ServerError,
            MraEisRecommendedAction.RetryLater,
            primaryCode: exception.HttpStatusCode,
            "MRA communication error",
            "A temporary MRA / network error occurred. The sale can remain queued for automatic retry.\n\n" +
            Truncate(exception.Message));
        LogEvaluation(retry);
        return retry;
    }

    private static MraEisResponseEvaluation BuildFromFieldError(
        int statusCode,
        string? remark,
        IReadOnlyList<EisApiError> errors,
        int fieldCode)
    {
        var fieldSummary = FormatFieldErrors(errors);
        if (fieldCode == MraEisStatusCodes.MissingMandatoryField)
        {
            return Build(
                statusCode,
                remark,
                errors,
                MraEisFailureCategory.MissingMandatoryField,
                MraEisRecommendedAction.QuarantinePayload,
                fieldCode,
                "Missing required MRA field",
                "MRA reported one or more mandatory fields are missing from the invoice payload. " +
                "The sale was quarantined so later invoices are not blocked.\n\n" + fieldSummary);
        }

        return Build(
            statusCode,
            remark,
            errors,
            MraEisFailureCategory.InvalidFieldValue,
            MraEisRecommendedAction.QuarantinePayload,
            fieldCode,
            "Invalid MRA field value",
            "MRA rejected a field value (length, range, or pattern). " +
            "Correct the highlighted fields in Queue Sync, then resubmit.\n\n" + fieldSummary);
    }

    /// <summary>
    /// statusCode=-2 (and similar) often carries correctable payload guidance only in <c>remark</c>
    /// (no field <c>errors[]</c>). Quarantine those instead of endless FIFO retries.
    /// </summary>
    private static MraEisResponseEvaluation BuildRemarkAwareUnknown(
        int statusCode,
        string? remark,
        IReadOnlyList<EisApiError> errors,
        int? fieldCode)
    {
        if (LooksLikeMissingPurchaseAuthorization(remark))
        {
            return Build(
                statusCode,
                remark,
                errors,
                MraEisFailureCategory.MissingMandatoryField,
                MraEisRecommendedAction.QuarantinePayload,
                fieldCode ?? statusCode,
                "Purchase Authorization Code required",
                "MRA rejected this B2B sale because the buyer TIN requires a Purchase Authorization Code. " +
                "Start a new B2B sale, enter Buyer TIN / Buyer name / Authorization Code, then complete the sale.\n\n" +
                Truncate(remark ?? string.Empty));
        }

        if (LooksLikeCorrectableCatalogMismatch(remark))
        {
            return Build(
                statusCode,
                remark,
                errors,
                MraEisFailureCategory.InvalidFieldValue,
                MraEisRecommendedAction.QuarantinePayload,
                fieldCode ?? statusCode,
                "Product catalog mismatch",
                "MRA rejected a product description or catalog field that does not match the site configuration. " +
                "Sync site products, then correct or re-ring the sale.\n\n" +
                Truncate(remark ?? string.Empty));
        }

        return BuildUnknown(statusCode, remark, errors, fieldCode);
    }

    private static bool LooksLikeMissingPurchaseAuthorization(string? remark) =>
        !string.IsNullOrWhiteSpace(remark)
        && remark.Contains("Purchase Authorization Code", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeCorrectableCatalogMismatch(string? remark) =>
        !string.IsNullOrWhiteSpace(remark)
        && (remark.Contains("doesn't match the one configured", StringComparison.OrdinalIgnoreCase)
            || remark.Contains("does not match the one configured", StringComparison.OrdinalIgnoreCase));

    private static MraEisResponseEvaluation BuildUnknown(
        int statusCode,
        string? remark,
        IReadOnlyList<EisApiError> errors,
        int? fieldCode)
    {
        var detail = FormatFieldErrors(errors);
        var body = string.IsNullOrWhiteSpace(detail)
            ? (remark ?? $"MRA returned statusCode {statusCode}.")
            : detail;

        // Unknown logical failures: quarantine when field errors exist; otherwise retry cautiously.
        var action = errors.Count > 0
            ? MraEisRecommendedAction.QuarantinePayload
            : MraEisRecommendedAction.RetryLater;

        return Build(
            statusCode,
            remark,
            errors,
            MraEisFailureCategory.Unknown,
            action,
            fieldCode ?? statusCode,
            "MRA request failed",
            Truncate(body));
    }

    private static MraEisResponseEvaluation Build(
        int statusCode,
        string? remark,
        IReadOnlyList<EisApiError> errors,
        MraEisFailureCategory category,
        MraEisRecommendedAction action,
        int primaryCode,
        string title,
        string operatorMessage)
    {
        var technical = BuildTechnicalDetail(statusCode, remark, errors, primaryCode);
        return new MraEisResponseEvaluation
        {
            IsSuccess = false,
            StatusCode = statusCode,
            Remark = remark,
            Category = category,
            RecommendedAction = action,
            PrimaryCode = primaryCode,
            Errors = errors,
            OperatorTitle = title,
            OperatorMessage = operatorMessage,
            TechnicalDetail = technical
        };
    }

    private void LogEvaluation(MraEisResponseEvaluation evaluation)
    {
        if (evaluation.IsSuccess)
        {
            return;
        }

        _logger.LogWarning(
            "MRA EIS failure classified. category={Category} action={Action} statusCode={StatusCode} primaryCode={PrimaryCode} remark={Remark} detail={Detail}",
            evaluation.Category,
            evaluation.RecommendedAction,
            evaluation.StatusCode,
            evaluation.PrimaryCode,
            evaluation.Remark ?? "(null)",
            evaluation.TechnicalDetail);
    }

    private static int? ResolvePrimaryFieldErrorCode(IReadOnlyList<EisApiError> errors)
    {
        if (errors.Count == 0)
        {
            return null;
        }

        // Prefer explicit MRA validation codes when several are present.
        foreach (var code in errors.Select(e => e.ErrorCode))
        {
            if (code is MraEisStatusCodes.MissingMandatoryField or MraEisStatusCodes.InvalidFieldValue)
            {
                return code;
            }
        }

        return errors[0].ErrorCode == 0 ? null : errors[0].ErrorCode;
    }

    private static string FormatFieldErrors(IReadOnlyList<EisApiError> errors)
    {
        if (errors.Count == 0)
        {
            return string.Empty;
        }

        var parts = errors
            .Take(8)
            .Select(e =>
            {
                var field = string.IsNullOrWhiteSpace(e.FieldName) ? null : e.FieldName.Trim();
                var msg = string.IsNullOrWhiteSpace(e.ErrorMessage)
                    ? $"errorCode {e.ErrorCode}"
                    : e.ErrorMessage.Trim();
                return field is null ? $"[{e.ErrorCode}] {msg}" : $"[{e.ErrorCode}] {field}: {msg}";
            });

        return string.Join("; ", parts);
    }

    private static string BuildTechnicalDetail(
        int statusCode,
        string? remark,
        IReadOnlyList<EisApiError> errors,
        int primaryCode)
    {
        var sb = new StringBuilder();
        sb.Append("statusCode=").Append(statusCode);
        sb.Append("; primaryCode=").Append(primaryCode);
        if (!string.IsNullOrWhiteSpace(remark))
        {
            sb.Append("; remark=").Append(remark.Trim());
        }

        var fields = FormatFieldErrors(errors);
        if (!string.IsNullOrWhiteSpace(fields))
        {
            sb.Append("; errors=").Append(fields);
        }

        return sb.ToString();
    }

    private static bool TryParseEnvelope(
        string? responseBody,
        out int statusCode,
        out string? remark,
        out List<EisApiError> errors)
    {
        statusCode = 0;
        remark = null;
        errors = [];

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("statusCode", out var sc) && sc.TryGetInt32(out var code))
            {
                statusCode = code;
            }

            if (root.TryGetProperty("remark", out var r) && r.ValueKind == JsonValueKind.String)
            {
                remark = r.GetString();
            }

            if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
            {
                foreach (var err in errs.EnumerateArray())
                {
                    var errorCode = err.TryGetProperty("errorCode", out var ec) && ec.TryGetInt32(out var c) ? c : 0;
                    var field = err.TryGetProperty("fieldName", out var f) ? f.GetString() : null;
                    var message = err.TryGetProperty("errorMessage", out var m) ? m.GetString() : null;
                    errors.Add(new EisApiError
                    {
                        ErrorCode = errorCode,
                        FieldName = field,
                        ErrorMessage = message
                    });
                }
            }

            return statusCode != 0 || errors.Count > 0 || !string.IsNullOrWhiteSpace(remark);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Truncate(string message) =>
        message.Length <= 400 ? message : message[..397] + "...";
}
