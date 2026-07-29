using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Services;

namespace PointOfSale.Infrastructure.Workers;

/// <summary>
/// Background FIFO synchronizer for dbo.OfflineInvoiceQueue (Albert Retail Terminal).
/// Monitors MRA connectivity and drains signed offline sales when the network is restored.
/// </summary>
public sealed class OfflineInvoiceFifoSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMraConnectivityMonitor _connectivity;
    private readonly OfflineSyncOptions _options;
    private readonly ILogger<OfflineInvoiceFifoSyncBackgroundService> _logger;
    private readonly object _wakeGate = new();
    private TaskCompletionSource _wakeSignal = NewWakeSignal();

    public OfflineInvoiceFifoSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<OfflineSyncOptions> options,
        ILogger<OfflineInvoiceFifoSyncBackgroundService> logger,
        IMraConnectivityMonitor? connectivity = null)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _connectivity = connectivity ?? new AlwaysReachableMraConnectivityMonitor();
        _connectivity.ReachabilityChanged += OnReachabilityChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Offline invoice FIFO sync worker started (interval {Interval}s, requireConnectivity={RequireConnectivity}).",
            _options.PollIntervalSeconds,
            _options.RequireMraConnectivity);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.RequireMraConnectivity && !_connectivity.IsMraReachable)
                {
                    _logger.LogDebug("Offline sync idle — waiting for MRA connectivity.");
                    await WaitForWakeOrDelayAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                OfflineSyncDrainResult drain;
                using (var scope = _scopeFactory.CreateScope())
                {
                    var sync = scope.ServiceProvider.GetRequiredService<OfflineTransactionSyncService>();
                    drain = await sync.DrainPendingAsync(stoppingToken).ConfigureAwait(false);
                }

                if (drain.ProcessedCount == 0 || drain.ConnectivityPaused)
                {
                    await WaitForWakeOrDelayAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Offline FIFO sync worker iteration failed.");
                await WaitForWakeOrDelayAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public override void Dispose()
    {
        _connectivity.ReachabilityChanged -= OnReachabilityChanged;
        base.Dispose();
    }

    private void OnReachabilityChanged(object? sender, EventArgs e)
    {
        if (!_connectivity.IsMraReachable)
        {
            return;
        }

        _logger.LogInformation("MRA connectivity restored — waking offline sync worker.");
        lock (_wakeGate)
        {
            _wakeSignal.TrySetResult();
            _wakeSignal = NewWakeSignal();
        }
    }

    private async Task WaitForWakeOrDelayAsync(CancellationToken stoppingToken)
    {
        Task wakeTask;
        lock (_wakeGate)
        {
            wakeTask = _wakeSignal.Task;
        }

        var delayTask = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollIntervalSeconds)), stoppingToken);
        await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
    }

    private static TaskCompletionSource NewWakeSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
