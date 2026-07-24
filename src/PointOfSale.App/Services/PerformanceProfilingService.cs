using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using static PointOfSale.App.Options.DiagnosticSeverities;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface IPerformanceProfilingService
{
    event EventHandler<PerformanceProfileSnapshot>? SnapshotUpdated;

    PerformanceProfileSnapshot? LatestSnapshot { get; }

    void RecordRenderedFrame();

    Task<PerformanceProfileSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default);

    Task<bool> FlushMetricsToCorporateEndpointAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TerminalFleetStatusEntry>> GetFleetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Samples CPU, memory, SQL latency, UI FPS, and error histograms; flushes anonymized metrics to corporate telemetry.
/// </summary>
public sealed class PerformanceProfilingService : IPerformanceProfilingService
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IDiagnosticTelemetryRepository _telemetryRepository;
    private readonly ITelemetryDiagnosticService _telemetry;
    private readonly TerminalDeploymentOptions _deployment;
    private readonly EnterprisePerformanceOptions _options;
    private readonly ILogger<PerformanceProfilingService> _logger;
    private readonly HttpClient _httpClient;
    private readonly object _frameLock = new();
    private readonly Queue<DateTime> _frameTimestamps = new();

    private PerformanceProfileSnapshot? _latest;
    private DateTime _lastCpuSampleUtc = DateTime.UtcNow;
    private TimeSpan _lastCpuTime = Process.GetCurrentProcess().TotalProcessorTime;

    public PerformanceProfilingService(
        ISqlConnectionFactory connectionFactory,
        IDiagnosticTelemetryRepository telemetryRepository,
        ITelemetryDiagnosticService telemetry,
        IOptions<TerminalDeploymentOptions> deployment,
        IOptions<EnterprisePerformanceOptions> options,
        ILogger<PerformanceProfilingService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _connectionFactory = connectionFactory;
        _telemetryRepository = telemetryRepository;
        _telemetry = telemetry;
        _deployment = deployment.Value;
        _options = options.Value;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient(nameof(PerformanceProfilingService));
        // Timeout is configured once via AddHttpClient — do not mutate after the client starts.
    }

    public event EventHandler<PerformanceProfileSnapshot>? SnapshotUpdated;

    public PerformanceProfileSnapshot? LatestSnapshot => _latest;

    public void RecordRenderedFrame()
    {
        if (!_options.Enabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var window = TimeSpan.FromSeconds(Math.Max(1, _options.UiFpsWindowSeconds));
        lock (_frameLock)
        {
            _frameTimestamps.Enqueue(now);
            while (_frameTimestamps.Count > 0 && now - _frameTimestamps.Peek() > window)
            {
                _frameTimestamps.Dequeue();
            }
        }
    }

    public async Task<PerformanceProfileSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var process = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var cpuDelta = process.TotalProcessorTime - _lastCpuTime;
        var timeDelta = now - _lastCpuSampleUtc;
        _lastCpuTime = process.TotalProcessorTime;
        _lastCpuSampleUtc = now;

        var cpuPercent = timeDelta.TotalMilliseconds > 1
            ? Math.Clamp(cpuDelta.TotalMilliseconds / (Environment.ProcessorCount * timeDelta.TotalMilliseconds) * 100d, 0, 100)
            : 0;

        var memoryMb = process.WorkingSet64 / (1024 * 1024);

        var queryLatency = await MeasureSqlLatencyAsync(cancellationToken).ConfigureAwait(false);
        var fps = CalculateUiFps();

        var histogram = await LoadErrorHistogramAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = new PerformanceProfileSnapshot
        {
            CapturedAtUtc = now,
            CpuUsagePercentage = Math.Round(cpuPercent, 1),
            MemoryConsumptionMb = memoryMb,
            AverageQueryLatencyMs = queryLatency,
            UiFramesPerSecond = Math.Round(fps, 1),
            ErrorsLastHour = histogram.Errors,
            WarningsLastHour = histogram.Warnings,
            TerminalId = _deployment.SiteId.Length > 0 ? _deployment.SiteId : "LOCAL",
            BranchId = _deployment.BranchId
        };

        _latest = snapshot;
        SnapshotUpdated?.Invoke(this, snapshot);

        await _telemetry.RecordDatabaseLatencyAsync(
                queryLatency,
                success: queryLatency < 10_000,
                detail: $"Performance profile sample (FPS {snapshot.UiFramesPerSecond:0.0})",
                cancellationToken)
            .ConfigureAwait(false);

        return snapshot;
    }

    public async Task<bool> FlushMetricsToCorporateEndpointAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.CorporateTelemetryEndpoint))
        {
            _logger.LogDebug("Corporate telemetry endpoint not configured; skipping flush.");
            return false;
        }

        var snapshot = _latest ?? await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var payload = new
        {
            snapshot.CapturedAtUtc,
            snapshot.CpuUsagePercentage,
            snapshot.MemoryConsumptionMb,
            snapshot.AverageQueryLatencyMs,
            snapshot.UiFramesPerSecond,
            snapshot.ErrorsLastHour,
            snapshot.WarningsLastHour,
            snapshot.TerminalId,
            snapshot.BranchId,
            Application = "AlbertRetailTerminal",
            SchemaVersion = 1
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.CorporateTelemetryEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_options.CorporateAuthorizationHeader))
        {
            request.Headers.TryAddWithoutValidation("Authorization", _options.CorporateAuthorizationHeader);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Corporate telemetry flush failed with HTTP {StatusCode}.",
                    (int)response.StatusCode);
                return false;
            }

            _logger.LogInformation("Flushed performance metrics to corporate telemetry endpoint.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Corporate telemetry flush failed.");
            return false;
        }
    }

    public async Task<IReadOnlyList<TerminalFleetStatusEntry>> GetFleetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var local = _latest ?? await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var list = new List<TerminalFleetStatusEntry>
        {
            new()
            {
                TerminalId = local.TerminalId,
                BranchId = local.BranchId,
                Status = local.AverageQueryLatencyMs > 1500 ? "Degraded" : "Healthy",
                CpuUsagePercentage = local.CpuUsagePercentage,
                MemoryConsumptionMb = local.MemoryConsumptionMb,
                AverageQueryLatencyMs = local.AverageQueryLatencyMs,
                LastSeenUtc = local.CapturedAtUtc,
                IsLocalTerminal = true
            }
        };

        if (string.IsNullOrWhiteSpace(_options.FleetStatusEndpoint))
        {
            return list;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.FleetStatusEndpoint);
            if (!string.IsNullOrWhiteSpace(_options.CorporateAuthorizationHeader))
            {
                request.Headers.TryAddWithoutValidation("Authorization", _options.CorporateAuthorizationHeader);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return list;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var remote = JsonSerializer.Deserialize<List<TerminalFleetStatusEntry>>(json);
            if (remote is { Count: > 0 })
            {
                foreach (var row in remote)
                {
                    row.IsLocalTerminal = string.Equals(row.TerminalId, local.TerminalId, StringComparison.OrdinalIgnoreCase);
                    list.Add(row);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Fleet status endpoint unavailable; showing local terminal only.");
        }

        return list;
    }

    private double CalculateUiFps()
    {
        lock (_frameLock)
        {
            if (_frameTimestamps.Count < 2)
            {
                return 0;
            }

            var span = _frameTimestamps.Last() - _frameTimestamps.Peek();
            if (span.TotalSeconds <= 0)
            {
                return 0;
            }

            return _frameTimestamps.Count / span.TotalSeconds;
        }
    }

    private async Task<int> MeasureSqlLatencyAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            cmd.CommandTimeout = 5;
            _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
    }

    private async Task<(int Errors, int Warnings)> LoadErrorHistogramAsync(CancellationToken cancellationToken)
    {
        var rows = await _telemetryRepository
            .GetRecentAsync(500, categoryFilter: null, severityFilter: null, search: null, cancellationToken)
            .ConfigureAwait(false);
        var cutoff = DateTime.UtcNow.AddHours(-1);
        var errors = 0;
        var warnings = 0;
        foreach (var row in rows.Where(r => r.CreatedAtUtc >= cutoff))
        {
            if (string.Equals(row.Severity, Error, StringComparison.OrdinalIgnoreCase)
                || string.Equals(row.Severity, Critical, StringComparison.OrdinalIgnoreCase))
            {
                errors++;
            }
            else if (string.Equals(row.Severity, Warning, StringComparison.OrdinalIgnoreCase))
            {
                warnings++;
            }
        }

        return (errors, warnings);
    }
}
