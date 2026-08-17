using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Http;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Options;

namespace PointOfSale.App.Services;

public interface IConnectionStatusService : IDisposable
{
    event EventHandler? StatusChanged;
    bool IsOnline { get; }
    bool IsMraReachable { get; }
    string StatusText { get; }
    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public sealed class ConnectionStatusService : IConnectionStatusService
{
    private readonly HttpClient _httpClient;
    private readonly MraApiOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConnectionStatusService> _logger;
    private readonly Timer _timer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _isOnline;
    private bool _isMraReachable;
    private string _statusText = "Checking…";
    private DateTime _lastPersistedReachableUtc = DateTime.MinValue;

    public ConnectionStatusService(
        IOptions<MraApiOptions> options,
        IServiceScopeFactory scopeFactory,
        ILogger<ConnectionStatusService> logger)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;

        var timeout = ResolveProbeTimeout(_options.HttpTimeout);
        // Reuse EIS HttpClient factory (TLS 1.2/1.3 + optional sandbox cert leniency).
        var handler = MraHttpClientFactory.CreateHandler(_options);
        if (handler is SocketsHttpHandler sockets)
        {
            sockets.ConnectTimeout = timeout;
        }

        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout
        };

        _timer = new Timer(
            async _ => await RefreshInternalAsync().ConfigureAwait(false),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(20));
    }

    public event EventHandler? StatusChanged;

    public bool IsOnline => _isOnline;
    public bool IsMraReachable => _isMraReachable;
    public string StatusText => _statusText;

    public Task RefreshAsync(CancellationToken cancellationToken = default) => RefreshInternalAsync(cancellationToken);

    /// <summary>Probe timeout follows MraEis:HttpTimeoutSeconds, floored at 30s.</summary>
    public static TimeSpan ResolveProbeTimeout(TimeSpan configured) =>
        configured < TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : configured;

    private async Task RefreshInternalAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _isOnline = NetworkInterface.GetIsNetworkAvailable();
            string? probeDetail = null;

            if (_isOnline && Uri.TryCreate(_options.ResolveBaseUrl(), UriKind.Absolute, out var baseUri))
            {
                (_isMraReachable, probeDetail) = await ProbeMraAsync(baseUri, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _isMraReachable = false;
            }

            var queueSuffix = await BuildQueueSuffixAsync(cancellationToken).ConfigureAwait(false);

            if (_isMraReachable)
            {
                await PersistLastMraReachableAsync(cancellationToken).ConfigureAwait(false);
            }

            _statusText = !_isOnline
                ? "Offline — no network"
                : _isMraReachable
                    ? $"Online — MRA reachable{queueSuffix}"
                    : $"Degraded — network up, MRA probe failed{(string.IsNullOrWhiteSpace(probeDetail) ? string.Empty : $" ({probeDetail})")}{queueSuffix}";

            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<(bool Reachable, string? Detail)> ProbeMraAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        // Prefer official EIS ping (JWT from TAC) when the terminal is activated.
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var ping = scope.ServiceProvider.GetService<MraEisPingService>();
            if (ping is not null)
            {
                var result = await ping.PingAsync(cancellationToken).ConfigureAwait(false);
                if (result.Attempted)
                {
                    var detail = result.Success
                        ? $"ping {(result.ElapsedMs?.ToString() ?? "?")}ms"
                        : Truncate(result.Detail ?? string.Empty, 64);
                    return (result.Reachable, detail);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "utilities/ping probe unavailable; falling back to HTTP GET.");
        }

        // Pre-activation / no JWT: probe API root and host origin.
        var candidates = new List<Uri> { baseUri };
        try
        {
            var origin = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/");
            if (!origin.Equals(baseUri))
            {
                candidates.Add(origin);
            }
        }
        catch
        {
            // Ignore malformed authority edges.
        }

        string? lastDetail = null;
        foreach (var uri in candidates)
        {
            var (reachable, detail) = await ProbeSingleEndpointAsync(uri, cancellationToken).ConfigureAwait(false);
            if (reachable)
            {
                return (true, detail);
            }

            lastDetail = detail;
        }

        return (false, lastDetail);
    }

    private async Task<(bool Reachable, string? Detail)> ProbeSingleEndpointAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            // Prefer GET headers-only — many EIS gateways reject or hang on HEAD.
            using var get = new HttpRequestMessage(HttpMethod.Get, uri);
            using var getResponse = await _httpClient.SendAsync(
                    get,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            var getCode = (int)getResponse.StatusCode;
            // Any HTTP response (including 401/404) proves TLS + TCP reachability to the EIS host.
            if (getCode > 0 && getCode < 600)
            {
                return (true, getCode.ToString());
            }

            return (false, $"HTTP {getCode}");
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("MRA probe timeout for {Uri} after {Timeout}.", uri, _httpClient.Timeout);
            return (false, "timeout");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "MRA probe HTTP failure for {Uri}.", uri);
            return (false, Truncate(ex.InnerException?.Message ?? ex.Message, 64));
        }
        catch (AuthenticationException ex)
        {
            _logger.LogWarning(ex, "MRA probe TLS failure for {Uri}.", uri);
            return (false, Truncate("TLS: " + ex.Message, 64));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MRA probe failed for {Uri}.", uri);
            return (false, Truncate(ex.Message, 64));
        }
    }

    private async Task PersistLastMraReachableAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        // Throttle DB writes — only on first success or every 5 minutes while reachable.
        if (_lastPersistedReachableUtc != DateTime.MinValue
            && now - _lastPersistedReachableUtc < TimeSpan.FromMinutes(5))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var config = scope.ServiceProvider.GetService<IConfigurationRepository>();
            if (config is null)
            {
                return;
            }

            await config.UpsertJsonAsync(
                    MraRuntimeConfigurationKeys.LastMraReachableUtc,
                    now.ToString("O"),
                    cancellationToken)
                .ConfigureAwait(false);
            _lastPersistedReachableUtc = now;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to persist last MRA reachable timestamp.");
        }
    }

    private async Task<string> BuildQueueSuffixAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetService<IOfflineInvoiceQueueRepository>();
            if (queue is null)
            {
                return string.Empty;
            }

            var counts = await queue.GetStatusCountsAsync(cancellationToken).ConfigureAwait(false);
            var pending = counts.GetValueOrDefault(OfflineQueueStatuses.Pending)
                + counts.GetValueOrDefault(OfflineQueueStatuses.Syncing);
            var quarantined = counts.GetValueOrDefault(OfflineQueueStatuses.Quarantined);

            if (pending == 0 && quarantined == 0)
            {
                return string.Empty;
            }

            if (quarantined > 0)
            {
                return $" · {pending} pending · {quarantined} quarantined";
            }

            return $" · {pending} pending";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        _timer.Dispose();
        _httpClient.Dispose();
        _refreshLock.Dispose();
    }
}
