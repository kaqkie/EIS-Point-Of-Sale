using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Serialization;
using PointOfSale.Mra.Services;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Fetches the official MRA terminal blocking message and applies lockout when EIS
/// sales responses set <c>shouldBlockTerminal</c> / <c>shouldBoardTerminal</c>.
/// </summary>
public sealed class TerminalBlockingMessageService
{
    private readonly MraApiClient _apiClient;
    private readonly IMraTerminalAuthProvider _authProvider;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ITerminalBlockingMessageResponseService _responseParser;
    private readonly MraRuntimeEnvironmentState? _runtimeState;
    private readonly ILogger<TerminalBlockingMessageService> _logger;

    public TerminalBlockingMessageService(
        MraApiClient apiClient,
        IMraTerminalAuthProvider authProvider,
        IConfigurationRepository configurationRepository,
        ILogger<TerminalBlockingMessageService> logger,
        MraRuntimeEnvironmentState? runtimeState = null,
        ITerminalBlockingMessageResponseService? responseParser = null)
    {
        _apiClient = apiClient;
        _authProvider = authProvider;
        _configurationRepository = configurationRepository;
        _logger = logger;
        _runtimeState = runtimeState;
        _responseParser = responseParser
            ?? new TerminalBlockingMessageResponseService(
                NullLogger<TerminalBlockingMessageResponseService>.Instance);
    }

    /// <summary>
    /// <c>POST /api/v1/utilities/get-terminal-blocking-message</c> —
    /// <c>Accept: text/plain</c>, JSON body with <c>terminalId</c>,
    /// <c>Authorization: Bearer {jwt}</c>.
    /// Parses the EIS envelope and evaluates whether sales must halt.
    /// </summary>
    public async Task<TerminalBlockingMessageResult> GetTerminalBlockingMessageAsync(
        GetTerminalBlockingMessageRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        var terminalId = FirstNonEmpty(request?.TerminalId);
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            terminalId = await _authProvider.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(terminalId))
        {
            return TerminalBlockingMessageResult.Failed(
                "No active terminalId is available for get-terminal-blocking-message.");
        }

        var payload = new GetTerminalBlockingMessageRequest { TerminalId = terminalId.Trim() };
        var signed = await _authProvider.GetSignedContextAsync(cancellationToken).ConfigureAwait(false);
        var context = new MraRequestContext
        {
            JwtToken = signed.JwtToken,
            SecretKey = signed.SecretKey,
            UseBearerAuthorization = true,
            AcceptHeader = "text/plain"
        };

        var response = await _apiClient
            .PostAsync<GetTerminalBlockingMessageRequest, TerminalBlockingMessageData>(
                "utilities/get-terminal-blocking-message",
                payload,
                context,
                cancellationToken)
            .ConfigureAwait(false);

        var parsed = _responseParser.Validate(
            new EisApiResponse<TerminalBlockingMessageData>
            {
                StatusCode = response.StatusCode,
                Remark = response.Remark,
                Data = response.Data,
                Errors = response.Errors
            });

        if (!parsed.Success || parsed.Data is null || parsed.Evaluation is null)
        {
            _logger.LogWarning(
                "get-terminal-blocking-message failed for terminalId={TerminalId}. Remark={Remark}",
                payload.TerminalId,
                parsed.Remark ?? "(null)");
            return TerminalBlockingMessageResult.Failed(
                parsed.Remark ?? "Unable to retrieve the MRA terminal blocking message.",
                parsed.StatusCode,
                parsed.Errors,
                payload.TerminalId);
        }

        _logger.LogInformation(
            "Retrieved terminal blocking message for terminalId={TerminalId} isBlocked={IsBlocked} blockedAt={BlockedAt}",
            payload.TerminalId,
            parsed.Data.IsBlocked,
            parsed.Data.BlockedAt);

        return TerminalBlockingMessageResult.Succeeded(
            payload.TerminalId,
            parsed.Data,
            parsed.Remark,
            parsed,
            _responseParser.BuildOperatorDisplay(parsed));
    }

    /// <summary>
    /// Parses a raw successful EIS JSON body and builds operator UI content for halt-sales messaging.
    /// </summary>
    public TerminalBlockingUiResult ProcessSuccessfulBlockingResponse(string? rawJson)
    {
        var parsed = _responseParser.ParseJson(rawJson);
        var display = _responseParser.BuildOperatorDisplay(parsed);
        return new TerminalBlockingUiResult
        {
            Success = parsed.Success,
            Parse = parsed,
            Display = display,
            ShouldHaltSales = display.ShouldHaltSales,
            IsBlocked = parsed.IsBlocked,
            BlockingReason = parsed.BlockingReason,
            BlockedAt = parsed.BlockedAt
        };
    }

    /// <summary>
    /// When a sales response requests terminal block/board, fetches the official explanation,
    /// persists lockout state, and returns cashier-facing guidance.
    /// </summary>
    public async Task<TerminalBlockHandlingResult> HandleSalesResponseAsync(
        SubmitSalesTransactionResponseData? salesResponse,
        CancellationToken cancellationToken = default)
    {
        if (salesResponse is null || !salesResponse.RequiresTerminalBlockHandling)
        {
            return TerminalBlockHandlingResult.NotRequired();
        }

        _logger.LogWarning(
            "Sales response requested terminal block (shouldBlockTerminal={Block} shouldBoardTerminal={Board}). Fetching official message.",
            salesResponse.ShouldBlockTerminal,
            salesResponse.ShouldBoardTerminal);

        TerminalBlockingMessageResult fetch;
        try
        {
            fetch = await GetTerminalBlockingMessageAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "get-terminal-blocking-message threw while handling sales block flag.");
            fetch = TerminalBlockingMessageResult.Failed(
                "MRA indicated this terminal must be blocked, but the blocking message could not be retrieved. Contact MRA / stop sales until cleared.");
        }

        var display = fetch.OperatorDisplay
            ?? (fetch.Parse is not null
                ? _responseParser.BuildOperatorDisplay(fetch.Parse)
                : new TerminalBlockingOperatorDisplay
                {
                    Title = "Terminal blocked by MRA",
                    Body = fetch.Remark
                        ?? "This terminal has been blocked by the Malawi Revenue Authority.",
                    ShouldHaltSales = true,
                    IsBlocked = true,
                    Severity = TerminalBlockingDisplaySeverity.Error
                });

        var reason = display.BlockingReason
            ?? fetch.Data?.ResolveOperatorMessage()
            ?? fetch.Remark
            ?? "This terminal has been blocked by the Malawi Revenue Authority.";
        var blockedAt = display.BlockedAt ?? fetch.Data?.BlockedAt ?? DateTime.UtcNow;
        var terminalId = fetch.TerminalId
            ?? await _authProvider.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);

        // Sales flags force lockout even if the utility unexpectedly returns isBlocked=false.
        var snapshot = new TerminalBlockingStateSnapshot
        {
            TerminalId = terminalId,
            IsBlocked = true,
            BlockingReason = reason,
            BlockedAt = blockedAt,
            CapturedUtc = DateTime.UtcNow,
            TriggeredByShouldBlockTerminal = salesResponse.ShouldBlockTerminal,
            TriggeredByShouldBoardTerminal = salesResponse.ShouldBoardTerminal,
            OfficialMessageRetrieved = fetch.Success
        };

        await PersistBlockingStateAsync(snapshot, cancellationToken).ConfigureAwait(false);
        _runtimeState?.SetTerminalBlocked(true, reason, blockedAt);

        return TerminalBlockHandlingResult.Blocked(snapshot, fetch, display);
    }

    public async Task<TerminalBlockingStateSnapshot?> GetPersistedBlockingStateAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.TerminalBlockingState, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TerminalBlockingStateSnapshot>(json, MraJson.SerializerOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Corrupt terminal blocking state; ignoring.");
            return null;
        }
    }

    public async Task ClearBlockingStateAsync(CancellationToken cancellationToken = default)
    {
        await _configurationRepository
            .UpsertJsonAsync(
                MraConfigurationKeys.TerminalBlockingState,
                JsonSerializer.Serialize(
                    new TerminalBlockingStateSnapshot { IsBlocked = false, CapturedUtc = DateTime.UtcNow },
                    MraJson.SerializerOptions),
                cancellationToken)
            .ConfigureAwait(false);
        _runtimeState?.SetTerminalBlocked(false, null, null);
    }

    private Task PersistBlockingStateAsync(
        TerminalBlockingStateSnapshot snapshot,
        CancellationToken cancellationToken) =>
        _configurationRepository.UpsertJsonAsync(
            MraConfigurationKeys.TerminalBlockingState,
            JsonSerializer.Serialize(snapshot, MraJson.SerializerOptions),
            cancellationToken);

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
}

public sealed class TerminalBlockingMessageResult
{
    public bool Success { get; init; }
    public string? TerminalId { get; init; }
    public string? Remark { get; init; }
    public int StatusCode { get; init; }
    public IReadOnlyList<EisApiError>? Errors { get; init; }
    public TerminalBlockingMessageData? Data { get; init; }
    public TerminalBlockingMessageParseResult? Parse { get; init; }
    public TerminalBlockingOperatorDisplay? OperatorDisplay { get; init; }

    public bool ShouldHaltSales =>
        OperatorDisplay?.ShouldHaltSales == true
        || Parse?.ShouldHaltSales == true
        || Data?.IsBlocked == true;

    public static TerminalBlockingMessageResult Succeeded(
        string terminalId,
        TerminalBlockingMessageData data,
        string? remark,
        TerminalBlockingMessageParseResult? parse = null,
        TerminalBlockingOperatorDisplay? operatorDisplay = null) =>
        new()
        {
            Success = true,
            StatusCode = 1,
            TerminalId = terminalId,
            Data = data,
            Remark = remark,
            Parse = parse,
            OperatorDisplay = operatorDisplay
        };

    public static TerminalBlockingMessageResult Failed(
        string remark,
        int statusCode = 0,
        IReadOnlyList<EisApiError>? errors = null,
        string? terminalId = null) =>
        new()
        {
            Success = false,
            StatusCode = statusCode,
            Remark = remark,
            Errors = errors,
            TerminalId = terminalId
        };
}

public sealed class TerminalBlockHandlingResult
{
    public bool Required { get; init; }
    public bool IsBlocked { get; init; }
    public TerminalBlockingStateSnapshot? State { get; init; }
    public TerminalBlockingMessageResult? Fetch { get; init; }
    public TerminalBlockingOperatorDisplay? Display { get; init; }

    public string? OperatorMessage =>
        Display?.Body
        ?? State?.BlockingReason
        ?? Fetch?.Data?.ResolveOperatorMessage()
        ?? Fetch?.Remark;

    public static TerminalBlockHandlingResult NotRequired() =>
        new() { Required = false, IsBlocked = false };

    public static TerminalBlockHandlingResult Blocked(
        TerminalBlockingStateSnapshot state,
        TerminalBlockingMessageResult fetch,
        TerminalBlockingOperatorDisplay? display = null) =>
        new()
        {
            Required = true,
            IsBlocked = true,
            State = state,
            Fetch = fetch,
            Display = display
        };
}

public sealed class TerminalBlockingUiResult
{
    public bool Success { get; init; }
    public bool ShouldHaltSales { get; init; }
    public bool IsBlocked { get; init; }
    public string? BlockingReason { get; init; }
    public DateTime? BlockedAt { get; init; }
    public TerminalBlockingMessageParseResult? Parse { get; init; }
    public TerminalBlockingOperatorDisplay? Display { get; init; }
}

public sealed class TerminalBlockingStateSnapshot
{
    public string? TerminalId { get; init; }
    public bool IsBlocked { get; init; }
    public string? BlockingReason { get; init; }
    public DateTime? BlockedAt { get; init; }
    public DateTime? CapturedUtc { get; init; }
    public bool TriggeredByShouldBlockTerminal { get; init; }
    public bool TriggeredByShouldBoardTerminal { get; init; }
    public bool OfficialMessageRetrieved { get; init; }
}
