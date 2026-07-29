using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Utilities;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// <c>POST /api/v1/utilities/ping</c> — authenticated EIS liveness check.
/// Authorization JWT is the token obtained from terminal activation (TAC).
/// </summary>
public sealed class MraEisPingService
{
    private readonly MraApiClient _apiClient;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ILogger<MraEisPingService> _logger;

    public MraEisPingService(
        MraApiClient apiClient,
        IConfigurationRepository configurationRepository,
        ILogger<MraEisPingService> logger)
    {
        _apiClient = apiClient;
        _configurationRepository = configurationRepository;
        _logger = logger;
    }

    /// <summary>
    /// Calls EIS ping with empty body, <c>Accept: text/plain</c>, and raw JWT Authorization
    /// (matches MRA samples). Returns <see cref="MraPingResult.Skipped"/> when no JWT is stored yet.
    /// </summary>
    public async Task<MraPingResult> PingAsync(CancellationToken cancellationToken = default)
    {
        var jwt = await _configurationRepository
            .GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(jwt))
        {
            return MraPingResult.Skipped("Terminal JWT is not available yet (complete activation first).");
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _apiClient
                .PostEmptyAsync<PingResponseData>(
                    "utilities/ping",
                    new MraRequestContext
                    {
                        JwtToken = jwt,
                        // Guide/sample: Authorization is the raw JWT from TAC (no Bearer prefix).
                        UseBearerAuthorization = false,
                        AcceptHeader = "text/plain"
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            sw.Stop();
            var elapsedMs = (int)sw.ElapsedMilliseconds;

            // Any HTTP response that deserialized proves the EIS host answered.
            if (response.IsSuccess)
            {
                _logger.LogDebug(
                    "utilities/ping ok in {ElapsedMs} ms. serverDate={ServerDate}",
                    elapsedMs,
                    response.Data?.ServerDate);
                return MraPingResult.Ok(elapsedMs, response.Data?.ServerDate, response.Remark);
            }

            _logger.LogWarning(
                "utilities/ping returned statusCode={StatusCode} Remark={Remark} in {ElapsedMs} ms",
                response.StatusCode,
                response.Remark ?? "(null)",
                elapsedMs);

            // Host responded — treat as reachable for connectivity monitors even if EIS rejected auth.
            return MraPingResult.HostReached(
                elapsedMs,
                response.StatusCode,
                response.Remark ?? "Ping rejected by EIS.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return MraPingResult.Unreachable((int)sw.ElapsedMilliseconds, "timeout");
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "utilities/ping HTTP failure after {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return MraPingResult.Unreachable((int)sw.ElapsedMilliseconds, ex.Message);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "utilities/ping failed after {ElapsedMs} ms", sw.ElapsedMilliseconds);
            return MraPingResult.Unreachable((int)sw.ElapsedMilliseconds, ex.Message);
        }
    }
}

public sealed record MraPingResult(
    bool Attempted,
    bool Reachable,
    bool Success,
    int? ElapsedMs,
    DateTime? ServerDate,
    int? StatusCode,
    string? Detail)
{
    public static MraPingResult Skipped(string detail) =>
        new(Attempted: false, Reachable: false, Success: false, ElapsedMs: null, ServerDate: null, StatusCode: null, Detail: detail);

    public static MraPingResult Ok(int elapsedMs, DateTime? serverDate, string? remark) =>
        new(Attempted: true, Reachable: true, Success: true, elapsedMs, serverDate, StatusCode: 1, Detail: remark);

    public static MraPingResult HostReached(int elapsedMs, int statusCode, string detail) =>
        new(Attempted: true, Reachable: true, Success: false, elapsedMs, ServerDate: null, statusCode, detail);

    public static MraPingResult Unreachable(int elapsedMs, string detail) =>
        new(Attempted: true, Reachable: false, Success: false, elapsedMs, ServerDate: null, StatusCode: null, Detail: detail);
}
