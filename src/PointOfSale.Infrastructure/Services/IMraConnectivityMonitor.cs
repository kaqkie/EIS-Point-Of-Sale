namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Abstraction so Infrastructure offline sync can pause/resume when MRA is unreachable
/// without taking a dependency on the WPF App layer.
/// </summary>
public interface IMraConnectivityMonitor
{
    bool IsMraReachable { get; }

    event EventHandler? ReachabilityChanged;

    Task RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default monitor used by tests / headless hosts — always reports reachable.
/// </summary>
public sealed class AlwaysReachableMraConnectivityMonitor : IMraConnectivityMonitor
{
    public bool IsMraReachable => true;

    public event EventHandler? ReachabilityChanged
    {
        add { }
        remove { }
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
