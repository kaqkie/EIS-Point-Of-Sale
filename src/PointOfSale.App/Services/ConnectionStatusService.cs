using System.Net.Http;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Options;
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
    private readonly Timer _timer;
    private bool _isOnline;
    private bool _isMraReachable;
    private string _statusText = "Checking…";

    public ConnectionStatusService(IOptions<MraApiOptions> options)
    {
        _options = options.Value;
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
        _isOnline = NetworkInterface.GetIsNetworkAvailable();
        _isMraReachable = false;

        if (_isOnline && Uri.TryCreate(_options.ResolveBaseUrl(), UriKind.Absolute, out var baseUri))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, baseUri);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                _isMraReachable = response.IsSuccessStatusCode || (int)response.StatusCode < 500;
            }
            catch
            {
                _isMraReachable = false;
            }
        }

        _statusText = !_isOnline
            ? "Offline — no network"
            : _isMraReachable
                ? "Online — MRA reachable"
                : "Offline — MRA unreachable";

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _timer.Dispose();
}
