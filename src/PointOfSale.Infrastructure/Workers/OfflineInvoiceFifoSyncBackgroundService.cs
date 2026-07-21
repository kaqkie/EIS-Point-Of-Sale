using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Services;

namespace PointOfSale.Infrastructure.Workers;

/// <summary>
/// Background FIFO synchronizer for dbo.OfflineInvoiceQueue (Albert Retail Terminal).
/// </summary>
public sealed class OfflineInvoiceFifoSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OfflineSyncOptions _options;
    private readonly ILogger<OfflineInvoiceFifoSyncBackgroundService> _logger;

    public OfflineInvoiceFifoSyncBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<OfflineSyncOptions> options,
        ILogger<OfflineInvoiceFifoSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Offline invoice FIFO sync worker started (interval {Interval}s).",
            _options.PollIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedAny = await ProcessPendingBatchAsync(stoppingToken).ConfigureAwait(false);
                if (!processedAny)
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Offline FIFO sync worker iteration failed.");
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<bool> ProcessPendingBatchAsync(CancellationToken cancellationToken)
    {
        var processed = false;
        while (true)
        {
            using var scope = _scopeFactory.CreateScope();
            var queueService = scope.ServiceProvider.GetRequiredService<OfflineSalesQueueService>();
            var didProcess = await queueService.ProcessNextFifoAsync(cancellationToken).ConfigureAwait(false);
            if (!didProcess)
            {
                break;
            }

            processed = true;
        }

        return processed;
    }
}
