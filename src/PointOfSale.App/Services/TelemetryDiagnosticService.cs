using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net.Http;
using System.Printing;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Options;
using Serilog;
using Serilog.Events;

namespace PointOfSale.App.Services;

public interface ITelemetryDiagnosticService
{
    event EventHandler<SystemHealthSnapshot>? HealthChanged;

    SystemHealthSnapshot? LatestSnapshot { get; }

    Task RecordExceptionAsync(
        string source,
        Exception exception,
        string severity = DiagnosticSeverities.Error,
        CancellationToken cancellationToken = default);

    Task RecordDatabaseLatencyAsync(int latencyMs, bool success, string? detail = null, CancellationToken cancellationToken = default);

    Task RecordWorkerHeartbeatAsync(string workerName, string? detail = null, CancellationToken cancellationToken = default);

    Task RecordMraConnectivityAsync(
        bool reachable,
        int? pingMs,
        string? httpStatus,
        string message,
        CancellationToken cancellationToken = default);

    Task RecordHealthCheckAsync(SystemHealthSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<SystemHealthSnapshot> RunDiagnosticsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DiagnosticTelemetryEvent>> GetRecentLogsAsync(
        string? categoryFilter = null,
        string? severityFilter = null,
        string? search = null,
        CancellationToken cancellationToken = default);

    Task PurgeExpiredAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Captures exceptions, SQL latencies, worker heartbeats, and MRA connectivity into Serilog + SQL Express.
/// </summary>
public sealed class TelemetryDiagnosticService : ITelemetryDiagnosticService
{
    private readonly IDiagnosticTelemetryRepository _repository;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IConnectionStatusService _connectionStatus;
    private readonly IOptions<ThermalPrinterOptions> _thermalOptions;
    private readonly IOptions<MraApiOptions> _mraOptions;
    private readonly SystemDiagnosticsOptions _options;
    private readonly ILogger<TelemetryDiagnosticService> _logger;
    private readonly HttpClient _httpClient;
    private readonly object _fileLock = new();
    private SystemHealthSnapshot? _latest;

    public TelemetryDiagnosticService(
        IDiagnosticTelemetryRepository repository,
        ISqlConnectionFactory connectionFactory,
        IConnectionStatusService connectionStatus,
        IOptions<ThermalPrinterOptions> thermalOptions,
        IOptions<MraApiOptions> mraOptions,
        IOptions<SystemDiagnosticsOptions> options,
        ILogger<TelemetryDiagnosticService> logger,
        IHttpClientFactory httpClientFactory)
    {
        _repository = repository;
        _connectionFactory = connectionFactory;
        _connectionStatus = connectionStatus;
        _thermalOptions = thermalOptions;
        _mraOptions = mraOptions;
        _options = options.Value;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient(nameof(TelemetryDiagnosticService));
        // Timeout is configured once via AddHttpClient — do not mutate after the client starts.
        EnsureDiagnosticDirectory();
    }

    public event EventHandler<SystemHealthSnapshot>? HealthChanged;

    public SystemHealthSnapshot? LatestSnapshot => _latest;

    public async Task RecordExceptionAsync(
        string source,
        Exception exception,
        string severity = DiagnosticSeverities.Error,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(
                DiagnosticEventCategories.Exception,
                severity,
                source,
                exception.Message,
                new { exception.GetType().FullName, exception.StackTrace },
                latencyMs: null,
                httpStatus: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task RecordDatabaseLatencyAsync(
        int latencyMs,
        bool success,
        string? detail = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            DiagnosticEventCategories.DatabaseLatency,
            success
                ? (latencyMs >= _options.DatabaseLatencyWarnMs
                    ? DiagnosticSeverities.Warning
                    : DiagnosticSeverities.Information)
                : DiagnosticSeverities.Error,
            "SqlExpress",
            detail ?? (success ? $"SQL round-trip {latencyMs} ms" : "SQL connectivity failed"),
            new { latencyMs, success },
            latencyMs,
            httpStatus: null,
            cancellationToken);

    public Task RecordWorkerHeartbeatAsync(
        string workerName,
        string? detail = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            DiagnosticEventCategories.WorkerHeartbeat,
            DiagnosticSeverities.Information,
            workerName,
            detail ?? "Heartbeat",
            new { workerName, at = DateTime.UtcNow },
            latencyMs: null,
            httpStatus: null,
            cancellationToken);

    public Task RecordMraConnectivityAsync(
        bool reachable,
        int? pingMs,
        string? httpStatus,
        string message,
        CancellationToken cancellationToken = default) =>
        WriteAsync(
            DiagnosticEventCategories.MraConnectivity,
            reachable ? DiagnosticSeverities.Information : DiagnosticSeverities.Warning,
            "MraEis",
            message,
            new { reachable, pingMs, httpStatus },
            pingMs,
            httpStatus,
            cancellationToken);

    public async Task RecordHealthCheckAsync(SystemHealthSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        _latest = snapshot;
        HealthChanged?.Invoke(this, snapshot);
        await WriteAsync(
                DiagnosticEventCategories.HealthCheck,
                snapshot.OverallHealthy ? DiagnosticSeverities.Information : DiagnosticSeverities.Warning,
                "HealthCheckWorker",
                snapshot.Summary,
                snapshot,
                snapshot.DatabaseLatencyMs,
                httpStatus: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<SystemHealthSnapshot> RunDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = new SystemHealthSnapshot { CheckedAtUtc = DateTime.UtcNow };

        // SQL Express
        var dbSw = Stopwatch.StartNew();
        try
        {
            await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT 1;";
            cmd.CommandTimeout = 5;
            _ = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            dbSw.Stop();
            snapshot.IsDatabaseHealthy = true;
            snapshot.DatabaseLatencyMs = (int)dbSw.ElapsedMilliseconds;
            await RecordDatabaseLatencyAsync(snapshot.DatabaseLatencyMs, success: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            dbSw.Stop();
            snapshot.IsDatabaseHealthy = false;
            snapshot.DatabaseLatencyMs = (int)dbSw.ElapsedMilliseconds;
            await RecordDatabaseLatencyAsync(snapshot.DatabaseLatencyMs, success: false, detail: ex.Message, cancellationToken)
                .ConfigureAwait(false);
            await RecordExceptionAsync("SqlExpress", ex, DiagnosticSeverities.Error, cancellationToken)
                .ConfigureAwait(false);
        }

        // Disk
        try
        {
            var root = Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            snapshot.DiskRoot = drive.Name;
            snapshot.AvailableDiskSpaceBytes = drive.AvailableFreeSpace;
            var freeMb = snapshot.AvailableDiskSpaceBytes / (1024L * 1024L);
            snapshot.IsDiskHealthy = freeMb >= _options.MinimumFreeDiskMegabytes;
            if (!snapshot.IsDiskHealthy)
            {
                await WriteAsync(
                        DiagnosticEventCategories.Disk,
                        DiagnosticSeverities.Warning,
                        "Disk",
                        $"Low disk space on {drive.Name}: {freeMb} MB free (min {_options.MinimumFreeDiskMegabytes} MB).",
                        new { freeMb, drive.Name },
                        latencyMs: null,
                        httpStatus: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            snapshot.IsDiskHealthy = false;
            snapshot.AvailableDiskSpaceBytes = 0;
            await RecordExceptionAsync("Disk", ex, DiagnosticSeverities.Warning, cancellationToken).ConfigureAwait(false);
        }

        // Thermal printer
        var printer = EvaluatePrinter();
        snapshot.IsPrinterHealthy = printer.Healthy;
        snapshot.PrinterStatus = printer.Status;
        if (!printer.Healthy)
        {
            await WriteAsync(
                    DiagnosticEventCategories.Printer,
                    DiagnosticSeverities.Warning,
                    "ThermalPrinter",
                    printer.Status,
                    printer,
                    latencyMs: null,
                    httpStatus: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // MRA gateway
        var mra = await ProbeMraAsync(cancellationToken).ConfigureAwait(false);
        snapshot.IsMraHealthy = mra.Reachable;
        snapshot.MraApiStatus = mra.StatusText;
        snapshot.MraPingMs = mra.PingMs;
        await RecordMraConnectivityAsync(
                mra.Reachable,
                mra.PingMs,
                mra.HttpStatus,
                mra.StatusText,
                cancellationToken)
            .ConfigureAwait(false);

        // Keep ConnectionStatusService in sync for the shell banner.
        try
        {
            await _connectionStatus.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // advisory
        }

        var issues = new List<string>();
        if (!snapshot.IsDatabaseHealthy)
        {
            issues.Add("SQL Express");
        }

        if (!snapshot.IsDiskHealthy)
        {
            issues.Add("Disk");
        }

        if (!snapshot.IsPrinterHealthy)
        {
            issues.Add("Printer");
        }

        if (!snapshot.IsMraHealthy)
        {
            issues.Add("MRA");
        }

        snapshot.Summary = issues.Count == 0
            ? $"All subsystems healthy (SQL {snapshot.DatabaseLatencyMs} ms, disk {FormatBytes(snapshot.AvailableDiskSpaceBytes)} free)."
            : $"Degraded: {string.Join(", ", issues)}.";

        await RecordHealthCheckAsync(snapshot, cancellationToken).ConfigureAwait(false);
        return snapshot;
    }

    public Task<IReadOnlyList<DiagnosticTelemetryEvent>> GetRecentLogsAsync(
        string? categoryFilter = null,
        string? severityFilter = null,
        string? search = null,
        CancellationToken cancellationToken = default) =>
        _repository.GetRecentAsync(_options.DashboardLogTake, categoryFilter, severityFilter, search, cancellationToken);

    public async Task PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, _options.TelemetryRetentionDays));
        var removed = await _repository.PurgeOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
        if (removed > 0)
        {
            _logger.LogInformation("Purged {Count} diagnostic telemetry rows older than {Cutoff:u}.", removed, cutoff);
        }
    }

    private async Task WriteAsync(
        string category,
        string severity,
        string source,
        string message,
        object? detail,
        int? latencyMs,
        string? httpStatus,
        CancellationToken cancellationToken)
    {
        var entry = new DiagnosticTelemetryEvent
        {
            Category = category,
            Severity = severity,
            Source = source,
            Message = Truncate(message, 500),
            DetailJson = Truncate(DiagnosticDetailJson.Serialize(detail), 4000),
            LatencyMs = latencyMs,
            HttpStatus = httpStatus
        };

        WriteSerilog(entry);
        WriteRotatedFile(entry);

        try
        {
            await _repository.InsertAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Database sink must not break the terminal; file/Serilog already captured the event.
            _logger.LogDebug(ex, "Failed to persist diagnostic telemetry to SQL Express.");
        }
    }

    private void WriteSerilog(DiagnosticTelemetryEvent entry)
    {
        var level = entry.Severity switch
        {
            DiagnosticSeverities.Critical => LogEventLevel.Fatal,
            DiagnosticSeverities.Error => LogEventLevel.Error,
            DiagnosticSeverities.Warning => LogEventLevel.Warning,
            _ => LogEventLevel.Information
        };

        Log.Write(
            level,
            "ART Diagnostic {Category} {Source} {LatencyMs} {HttpStatus}: {Message}",
            entry.Category,
            entry.Source,
            entry.LatencyMs,
            entry.HttpStatus,
            entry.Message);
    }

    private void WriteRotatedFile(DiagnosticTelemetryEvent entry)
    {
        try
        {
            var dir = EnsureDiagnosticDirectory();
            var path = Path.Combine(dir, $"diagnostics-{DateTime.UtcNow:yyyyMMdd}.log");
            var line =
                $"{DateTime.UtcNow:O}\t{entry.Severity}\t{entry.Category}\t{entry.Source}\t{entry.LatencyMs}\t{entry.HttpStatus}\t{entry.Message}{Environment.NewLine}";
            lock (_fileLock)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }

            PurgeOldDiagnosticFiles(dir);
        }
        catch
        {
            // disk logging is best-effort
        }
    }

    private string EnsureDiagnosticDirectory()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, _options.DiagnosticLogDirectory);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void PurgeOldDiagnosticFiles(string directory)
    {
        var retainDays = Math.Max(1, _options.DiagnosticFileRetainedDays);
        var cutoff = DateTime.UtcNow.AddDays(-retainDays);
        foreach (var file in Directory.EnumerateFiles(directory, "diagnostics-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private (bool Healthy, string Status) EvaluatePrinter()
    {
        var opts = _thermalOptions.Value;
        if (!opts.Enabled)
        {
            return (true, "Thermal printer disabled (OK).");
        }

        try
        {
            if (opts.ConnectionMode == ThermalPrinterConnectionMode.Serial)
            {
                var ports = SerialPort.GetPortNames();
                var ok = ports.Any(p => p.Equals(opts.SerialPortName, StringComparison.OrdinalIgnoreCase));
                return ok
                    ? (true, $"Serial port {opts.SerialPortName} available.")
                    : (false, $"Serial port {opts.SerialPortName} not found.");
            }

            using var server = new LocalPrintServer();
            if (!string.IsNullOrWhiteSpace(opts.PrinterName))
            {
                var queue = server.GetPrintQueues().FirstOrDefault(q =>
                    q.FullName.Equals(opts.PrinterName.Trim(), StringComparison.OrdinalIgnoreCase)
                    || q.Name.Equals(opts.PrinterName.Trim(), StringComparison.OrdinalIgnoreCase));
                return queue is null
                    ? (false, $"Printer queue '{opts.PrinterName}' not found.")
                    : (true, $"Printer queue '{queue.FullName}' ready.");
            }

            return server.DefaultPrintQueue is null
                ? (false, "No default Windows printer configured.")
                : (true, $"Default printer '{server.DefaultPrintQueue.FullName}' ready.");
        }
        catch (Exception ex)
        {
            return (false, $"Printer check failed: {ex.Message}");
        }
    }

    private async Task<(bool Reachable, string StatusText, int? PingMs, string? HttpStatus)> ProbeMraAsync(
        CancellationToken cancellationToken)
    {
        var baseUrl = _mraOptions.Value.ResolveBaseUrl();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return (false, "MRA base URL is not configured.", null, null);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            // GET headers-only — HEAD is often rejected/hung by EIS gateways.
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            sw.Stop();
            var code = ((int)response.StatusCode).ToString();
            // Any HTTP status proves TCP/TLS reachability.
            var ok = (int)response.StatusCode is > 0 and < 600;
            return ok
                ? (true, $"MRA reachable ({code}) in {sw.ElapsedMilliseconds} ms.", (int)sw.ElapsedMilliseconds, code)
                : (false, $"MRA HTTP {code} in {sw.ElapsedMilliseconds} ms.", (int)sw.ElapsedMilliseconds, code);
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return (false, $"MRA timeout after {sw.ElapsedMilliseconds} ms.", (int)sw.ElapsedMilliseconds, "Timeout");
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            return (false, $"MRA unreachable: {ex.Message}", (int)sw.ElapsedMilliseconds, "HttpRequestException");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, $"MRA probe failed: {ex.Message}", (int)sw.ElapsedMilliseconds, ex.GetType().Name);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        } while (value >= 1024 && unit < units.Length - 1);

        return $"{value:0.##} {units[unit]}";
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
