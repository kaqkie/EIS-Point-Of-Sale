using System.Text.Json;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Serialization;

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
    private readonly MraRuntimeEnvironmentState? _runtimeState;
    private readonly ILogger<TerminalBlockingMessageService> _logger;

    public TerminalBlockingMessageService(
        MraApiClient apiClient,
        IMraTerminalAuthProvider authProvider,
        IConfigurationRepository configurationRepository,
        ILogger<TerminalBlockingMessageService> logger,
        MraRuntimeEnvironmentState? runtimeState = null)
    {
        _apiClient = apiClient;
        _authProvider = authProvider;
        _configurationRepository = configurationRepository;
        _logger = logger;
        _runtimeState = runtimeState;
    }

    /// <summary>
    /// <c>POST /api/v1/utilities/get-terminal-blocking-message</c> —
    /// <c>Accept: text/plain</c>, JSON body with <c>terminalId</c>,
    /// <c>Authorization: Bearer {jwt}</c>.
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

        if (!response.IsSuccess || response.Data is null)
        {
            _logger.LogWarning(
                "get-terminal-blocking-message failed for terminalId={TerminalId}. Remark={Remark}",
                payload.TerminalId,
                response.Remark ?? "(null)");
            return TerminalBlockingMessageResult.Failed(
                response.Remark ?? "Unable to retrieve the MRA terminal blocking message.",
                response.StatusCode,
                response.Errors,
                payload.TerminalId);
        }

        _logger.LogInformation(
            "Retrieved terminal blocking message for terminalId={TerminalId} isBlocked={IsBlocked} blockedAt={BlockedAt}",
            payload.TerminalId,
            response.Data.IsBlocked,
            response.Data.BlockedAt);

        return TerminalBlockingMessageResult.Succeeded(
            payload.TerminalId,
            response.Data,
            response.Remark);
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

        var reason = fetch.Data?.ResolveOperatorMessage()
            ?? fetch.Remark
            ?? "This terminal has been blocked by the Malawi Revenue Authority.";
        var blockedAt = fetch.Data?.BlockedAt ?? DateTime.UtcNow;
        var terminalId = fetch.TerminalId
            ?? await _authProvider.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);

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

        return TerminalBlockHandlingResult.Blocked(snapshot, fetch);
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
    public IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? Errors { get; init; }
    public TerminalBlockingMessageData? Data { get; init; }

    public static TerminalBlockingMessageResult Succeeded(
        string terminalId,
        TerminalBlockingMessageData data,
        string? remark) =>
        new()
        {
            Success = true,
            StatusCode = 1,
            TerminalId = terminalId,
            Data = data,
            Remark = remark
        };

    public static TerminalBlockingMessageResult Failed(
        string remark,
        int statusCode = 0,
        IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? errors = null,
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

    public string? OperatorMessage =>
        State?.BlockingReason
        ?? Fetch?.Data?.ResolveOperatorMessage()
        ?? Fetch?.Remark;

    public static TerminalBlockHandlingResult NotRequired() =>
        new() { Required = false, IsBlocked = false };

    public static TerminalBlockHandlingResult Blocked(
        TerminalBlockingStateSnapshot state,
        TerminalBlockingMessageResult fetch) =>
        new()
        {
            Required = true,
            IsBlocked = true,
            State = state,
            Fetch = fetch
        };
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
