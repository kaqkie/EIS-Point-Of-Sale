using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;

namespace PointOfSale.App.Services;

/// <summary>
/// Periodically samples performance metrics and flushes anonymized telemetry to corporate endpoints.
/// </summary>
public sealed class PerformanceProfilingBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EnterprisePerformanceOptions _options;
    private readonly ILogger<PerformanceProfilingBackgroundService> _logger;
    private DateTime _lastFlushUtc = DateTime.MinValue;

    public PerformanceProfilingBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<EnterprisePerformanceOptions> options,
        ILogger<PerformanceProfilingBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("PerformanceProfilingBackgroundService is disabled.");
            return;
        }

        var sampleInterval = TimeSpan.FromSeconds(Math.Max(10, _options.ProfilingIntervalSeconds));
        var flushInterval = TimeSpan.FromSeconds(Math.Max(60, _options.TelemetryFlushIntervalSeconds));
        _logger.LogInformation(
            "PerformanceProfilingBackgroundService started (sample {Sample}s, flush {Flush}s).",
            sampleInterval.TotalSeconds,
            flushInterval.TotalSeconds);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var profiling = scope.ServiceProvider.GetRequiredService<IPerformanceProfilingService>();
                await profiling.CaptureSnapshotAsync(stoppingToken).ConfigureAwait(false);

                if (DateTime.UtcNow - _lastFlushUtc >= flushInterval)
                {
                    await profiling.FlushMetricsToCorporateEndpointAsync(stoppingToken).ConfigureAwait(false);
                    _lastFlushUtc = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Performance profiling iteration failed.");
            }

            try
            {
                await Task.Delay(sampleInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
