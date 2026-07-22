using System.Net.Http;
using System.Net.NetworkInformation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
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
    private readonly Timer _timer;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private bool _isOnline;
    private bool _isMraReachable;
    private string _statusText = "Checking…";

    public ConnectionStatusService(IOptions<MraApiOptions> options, IServiceScopeFactory scopeFactory)
    {
        _options = options.Value;
        _scopeFactory = scopeFactory;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _timer = new Timer(async _ => await RefreshInternalAsync().ConfigureAwait(false), null, TimeSpan.Zero, TimeSpan.FromSeconds(20));
    }

    public event EventHandler? StatusChanged;

    public bool IsOnline => _isOnline;
    public bool IsMraReachable => _isMraReachable;
    public string StatusText => _statusText;

    public Task RefreshAsync(CancellationToken cancellationToken = default) => RefreshInternalAsync(cancellationToken);

    private async Task RefreshInternalAsync(CancellationToken cancellationToken = default)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            _isOnline = NetworkInterface.GetIsNetworkAvailable();
            _isMraReachable = false;
            string? probeDetail = null;

            if (_isOnline && Uri.TryCreate(_options.ResolveBaseUrl(), UriKind.Absolute, out var baseUri))
            {
                (_isMraReachable, probeDetail) = await ProbeMraAsync(baseUri, cancellationToken).ConfigureAwait(false);
            }

            var queueSuffix = await BuildQueueSuffixAsync(cancellationToken).ConfigureAwait(false);

            _statusText = !_isOnline
                ? "Offline — no network"
                : _isMraReachable
                    ? $"Online — MRA reachable{queueSuffix}"
                    : $"Offline — MRA unreachable{(string.IsNullOrWhiteSpace(probeDetail) ? string.Empty : $" ({probeDetail})")}{queueSuffix}";

            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<(bool Reachable, string? Detail)> ProbeMraAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, baseUri);
            using var headResponse = await _httpClient.SendAsync(head, cancellationToken).ConfigureAwait(false);
            var headCode = (int)headResponse.StatusCode;
            if (headResponse.IsSuccessStatusCode || headCode < 500)
            {
                return (true, headCode.ToString());
            }

            // Some gateways reject HEAD; fall back to a lightweight GET for connectivity health.
            using var get = new HttpRequestMessage(HttpMethod.Get, baseUri);
            using var getResponse = await _httpClient.SendAsync(
                    get,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            var getCode = (int)getResponse.StatusCode;
            return getResponse.IsSuccessStatusCode || getCode < 500
                ? (true, getCode.ToString())
                : (false, $"HTTP {getCode}");
        }
        catch (TaskCanceledException)
        {
            return (false, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return (false, Truncate(ex.Message, 48));
        }
        catch (Exception ex)
        {
            return (false, Truncate(ex.Message, 48));
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
