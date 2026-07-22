using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;

namespace PointOfSale.App.Services;

/// <summary>
/// Periodically health-checks SQL Express, disk capacity, thermal printer, and MRA gateway.
/// </summary>
public sealed class HealthCheckWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SystemDiagnosticsOptions _options;
    private readonly ILogger<HealthCheckWorker> _logger;

    public HealthCheckWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<SystemDiagnosticsOptions> options,
        ILogger<HealthCheckWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("HealthCheckWorker is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(15, _options.HealthCheckIntervalSeconds));
        _logger.LogInformation("HealthCheckWorker started (interval {Interval}s).", interval.TotalSeconds);

        // Stagger first run slightly after startup so bootstrap can finish.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken).ConfigureAwait(false);
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
                var telemetry = scope.ServiceProvider.GetRequiredService<ITelemetryDiagnosticService>();
                await telemetry.RecordWorkerHeartbeatAsync(nameof(HealthCheckWorker), cancellationToken: stoppingToken)
                    .ConfigureAwait(false);
                var snapshot = await telemetry.RunDiagnosticsAsync(stoppingToken).ConfigureAwait(false);
                await telemetry.PurgeExpiredAsync(stoppingToken).ConfigureAwait(false);

                if (!snapshot.OverallHealthy)
                {
                    _logger.LogWarning("Health check degraded: {Summary}", snapshot.Summary);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HealthCheckWorker iteration failed.");
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var telemetry = scope.ServiceProvider.GetRequiredService<ITelemetryDiagnosticService>();
                    await telemetry.RecordExceptionAsync(nameof(HealthCheckWorker), ex, cancellationToken: stoppingToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // ignore nested failures
                }
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
