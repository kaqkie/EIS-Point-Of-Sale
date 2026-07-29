using PointOfSale.Infrastructure.Services;

namespace PointOfSale.App.Services;

/// <summary>
/// Bridges WPF <see cref="IConnectionStatusService"/> into Infrastructure offline sync.
/// </summary>
public sealed class ConnectionStatusMraConnectivityMonitor : IMraConnectivityMonitor
{
    private readonly IConnectionStatusService _connectionStatus;

    public ConnectionStatusMraConnectivityMonitor(IConnectionStatusService connectionStatus)
    {
        _connectionStatus = connectionStatus;
        _connectionStatus.StatusChanged += (_, _) => ReachabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool IsMraReachable => _connectionStatus.IsMraReachable;

    public event EventHandler? ReachabilityChanged;

    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        _connectionStatus.RefreshAsync(cancellationToken);
}
