using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Mra.Services;

public interface ITerminalBlockingMessageResponseService
{
    /// <summary>Deserializes a raw EIS JSON body into the typed blocking-message envelope.</summary>
    TerminalBlockingMessageParseResult ParseJson(string? json);

    /// <summary>Validates an already-deserialized EIS envelope and evaluates block status for the UI.</summary>
    TerminalBlockingMessageParseResult Validate(EisApiResponse<TerminalBlockingMessageData>? response);

    /// <summary>
    /// Extracts <c>isBlocked</c>, <c>blockingReason</c>, and <c>blockedAt</c> so Albert Retail Terminal
    /// can halt sales and show the official compliance message to the operator.
    /// </summary>
    TerminalBlockingEvaluation EvaluateBlockingStatus(TerminalBlockingMessageData data);

    /// <summary>
    /// Builds cashier/operator display content (title + body) from a successful parse result.
    /// </summary>
    TerminalBlockingOperatorDisplay BuildOperatorDisplay(TerminalBlockingMessageParseResult parsed);
}

/// <summary>
/// Parses and evaluates MRA EIS <c>get-terminal-blocking-message</c> responses for
/// Albert Retail Terminal compliance lockout / cashier messaging.
/// </summary>
public sealed class TerminalBlockingMessageResponseService : ITerminalBlockingMessageResponseService
{
    private readonly ILogger<TerminalBlockingMessageResponseService> _logger;

    public TerminalBlockingMessageResponseService(ILogger<TerminalBlockingMessageResponseService> logger)
    {
        _logger = logger;
    }

    public TerminalBlockingMessageParseResult ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return TerminalBlockingMessageParseResult.Failed("Empty MRA response body.");
        }

        try
        {
            var response = JsonSerializer.Deserialize<GetTerminalBlockingMessageResponse>(
                json,
                MraJson.SerializerOptions);
            return Validate(response);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize get-terminal-blocking-message JSON.");
            return TerminalBlockingMessageParseResult.Failed(
                "MRA get-terminal-blocking-message response was not valid JSON.",
                ex.Message);
        }
    }

    public TerminalBlockingMessageParseResult Validate(EisApiResponse<TerminalBlockingMessageData>? response)
    {
        if (response is null)
        {
            return TerminalBlockingMessageParseResult.Failed("MRA response deserialized to null.");
        }

        if (!response.IsSuccess)
        {
            var errorDetail = FormatErrors(response.Errors);
            _logger.LogWarning(
                "get-terminal-blocking-message logical failure. statusCode={StatusCode} remark={Remark} errors={Errors}",
                response.StatusCode,
                response.Remark ?? "(null)",
                errorDetail);
            return TerminalBlockingMessageParseResult.Failed(
                response.Remark ?? $"MRA returned statusCode {response.StatusCode}.",
                errorDetail,
                response.StatusCode,
                response.Errors);
        }

        if (response.Data is null)
        {
            return TerminalBlockingMessageParseResult.Failed(
                "MRA success response contained no terminal blocking data.",
                statusCode: response.StatusCode);
        }

        var evaluation = EvaluateBlockingStatus(response.Data);
        _logger.LogInformation(
            "Parsed terminal blocking message isBlocked={IsBlocked} blockedAt={BlockedAt} haltSales={HaltSales}",
            evaluation.IsBlocked,
            evaluation.BlockedAt,
            evaluation.ShouldHaltSales);

        return TerminalBlockingMessageParseResult.Succeeded(
            response.Data,
            evaluation,
            response.Remark,
            response.StatusCode);
    }

    public TerminalBlockingEvaluation EvaluateBlockingStatus(TerminalBlockingMessageData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var reason = string.IsNullOrWhiteSpace(data.BlockingReason)
            ? null
            : data.BlockingReason.Trim();
        var operatorMessage = data.ResolveOperatorMessage();
        var blockedAt = data.BlockedAt?.ToUniversalTime();

        // Official endpoint: when isBlocked is true, sales must stop. If MRA returns a reason
        // without isBlocked (defensive), still treat as blocked for cashier safety.
        var isBlocked = data.IsBlocked || !string.IsNullOrWhiteSpace(reason);
        var shouldHaltSales = isBlocked;

        string message;
        if (!isBlocked)
        {
            message = "Terminal is not blocked. Sales processing may continue.";
        }
        else if (!string.IsNullOrWhiteSpace(reason))
        {
            message = reason;
        }
        else
        {
            message = operatorMessage;
        }

        return new TerminalBlockingEvaluation
        {
            IsBlocked = isBlocked,
            ShouldHaltSales = shouldHaltSales,
            BlockingReason = reason,
            BlockedAt = blockedAt,
            OperatorMessage = message,
            OperatorTitle = isBlocked ? "Terminal blocked by MRA" : "Terminal not blocked"
        };
    }

    public TerminalBlockingOperatorDisplay BuildOperatorDisplay(TerminalBlockingMessageParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        if (!parsed.Success || parsed.Evaluation is null)
        {
            return new TerminalBlockingOperatorDisplay
            {
                Title = "Unable to load blocking message",
                Body =
                    "MRA indicated a terminal restriction, but the official blocking message could not be parsed.\n\n" +
                    Truncate(parsed.Remark ?? parsed.ErrorDetail ?? "Unknown parse error.") +
                    "\n\nStop sales and contact MRA Taxpayer Services until the terminal status is confirmed.",
                ShouldHaltSales = true,
                Severity = TerminalBlockingDisplaySeverity.Error
            };
        }

        var evaluation = parsed.Evaluation;
        if (!evaluation.ShouldHaltSales)
        {
            return new TerminalBlockingOperatorDisplay
            {
                Title = evaluation.OperatorTitle ?? "Terminal not blocked",
                Body = evaluation.OperatorMessage ?? "Terminal is not blocked.",
                ShouldHaltSales = false,
                IsBlocked = false,
                BlockingReason = evaluation.BlockingReason,
                BlockedAt = evaluation.BlockedAt,
                Severity = TerminalBlockingDisplaySeverity.Information
            };
        }

        var when = evaluation.BlockedAt is DateTime at
            ? $"\n\nBlocked at (UTC): {at:yyyy-MM-dd HH:mm:ss}"
            : string.Empty;

        return new TerminalBlockingOperatorDisplay
        {
            Title = evaluation.OperatorTitle ?? "Terminal blocked by MRA",
            Body =
                "Albert Retail Terminal must stop all sales processing until MRA unblocks this terminal.\n\n" +
                "Official MRA explanation:\n" +
                Truncate(evaluation.OperatorMessage ?? evaluation.BlockingReason ?? "No blocking reason was returned.") +
                when +
                "\n\nNext steps: contact MRA / your tax consultant, then use Check Terminal Unblock Status once cleared.",
            ShouldHaltSales = true,
            IsBlocked = true,
            BlockingReason = evaluation.BlockingReason,
            BlockedAt = evaluation.BlockedAt,
            Severity = TerminalBlockingDisplaySeverity.Error
        };
    }

    private static string Truncate(string message) =>
        message.Length <= 400 ? message : message[..397] + "...";

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

public sealed class TerminalBlockingEvaluation
{
    public bool IsBlocked { get; init; }
    public bool ShouldHaltSales { get; init; }
    public string? BlockingReason { get; init; }
    public DateTime? BlockedAt { get; init; }
    public string? OperatorTitle { get; init; }
    public string? OperatorMessage { get; init; }
}

public sealed class TerminalBlockingMessageParseResult
{
    public bool Success { get; init; }
    public int StatusCode { get; init; }
    public string? Remark { get; init; }
    public string? ErrorDetail { get; init; }
    public IReadOnlyList<EisApiError>? Errors { get; init; }
    public TerminalBlockingMessageData? Data { get; init; }
    public TerminalBlockingEvaluation? Evaluation { get; init; }

    public bool IsBlocked => Evaluation?.IsBlocked == true;
    public bool ShouldHaltSales => Evaluation?.ShouldHaltSales == true;
    public string? BlockingReason => Evaluation?.BlockingReason ?? Data?.BlockingReason;
    public DateTime? BlockedAt => Evaluation?.BlockedAt ?? Data?.BlockedAt;

    public static TerminalBlockingMessageParseResult Succeeded(
        TerminalBlockingMessageData data,
        TerminalBlockingEvaluation evaluation,
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

    public static TerminalBlockingMessageParseResult Failed(
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

public enum TerminalBlockingDisplaySeverity
{
    Information,
    Warning,
    Error
}

public sealed class TerminalBlockingOperatorDisplay
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public bool ShouldHaltSales { get; init; }
    public bool IsBlocked { get; init; }
    public string? BlockingReason { get; init; }
    public DateTime? BlockedAt { get; init; }
    public TerminalBlockingDisplaySeverity Severity { get; init; }
}
