using System.IO;
using System.Net.NetworkInformation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface ISystemHealthMonitorService
{
    event EventHandler<SystemHealthMonitorSnapshot>? StatusChanged;
    event EventHandler<SystemHealthAlert>? AlertRaised;

    SystemHealthMonitorSnapshot? LatestSnapshot { get; }
    IReadOnlyList<SystemHealthAlert> RecentAlerts { get; }

    /// <summary>
    /// Runs a full health evaluation: SQL Express, disk, internet, MRA gateway, offline sync queue.
    /// </summary>
    Task<SystemHealthMonitorSnapshot> EvaluateAsync(CancellationToken cancellationToken = default);

    Task<SystemHealthMonitorSnapshot> RunManualDiagnosticAsync(CancellationToken cancellationToken = default);
}

public sealed record SystemHealthMonitorSnapshot
{
    public DateTime CheckedAtUtc { get; init; } = DateTime.UtcNow;

    public bool IsDatabaseHealthy { get; init; }
    public int DatabaseLatencyMs { get; init; }

    public bool IsInternetOnline { get; init; }
    public bool IsMraApiReachable { get; init; }
    public string MraApiStatus { get; init; } = "Unknown";
    public int? MraPingMs { get; init; }

    public double DiskSpaceFreeMb { get; init; }
    public bool IsDiskHealthy { get; init; }
    public string DiskRoot { get; init; } = string.Empty;

    public double BackupVolumeFreeMb { get; init; }
    public string BackupDirectory { get; init; } = string.Empty;

    public int ActiveSyncQueueCount { get; init; }
    public int PendingQueueCount { get; init; }
    public int SyncingQueueCount { get; init; }
    public int QuarantinedQueueCount { get; init; }
    public bool IsQueueHealthy { get; init; }

    public bool IsPrinterHealthy { get; init; }
    public string PrinterStatus { get; init; } = string.Empty;

    public bool OverallHealthy { get; init; }
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<string> WarningMessages { get; init; } = Array.Empty<string>();
}

public sealed class SystemHealthAlert
{
    public DateTime RaisedAtUtc { get; init; } = DateTime.UtcNow;
    public string Code { get; init; } = string.Empty;
    public string Severity { get; init; } = DiagnosticSeverities.Warning;
    public string Message { get; init; } = string.Empty;
}

public static class SystemHealthAlertCodes
{
    public const string DatabaseUnhealthy = "DATABASE_UNHEALTHY";
    public const string DatabaseLatency = "DATABASE_LATENCY";
    public const string DiskLow = "DISK_LOW";
    public const string InternetOffline = "INTERNET_OFFLINE";
    public const string MraUnreachable = "MRA_UNREACHABLE";
    public const string QueueBacklog = "QUEUE_BACKLOG";
    public const string BackupVolumeLow = "BACKUP_VOLUME_LOW";
}

/// <summary>
/// Phase 36 proactive health monitor — aggregates SQL Express, disk, internet, MRA EIS,
/// and offline sync queue into a live status stream with automated alerts.
/// </summary>
public sealed class SystemHealthMonitorService : ISystemHealthMonitorService
{
    private readonly ITelemetryDiagnosticService _telemetry;
    private readonly IConnectionStatusService _connectionStatus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDatabaseBackupService _backupService;
    private readonly SystemDiagnosticsOptions _options;
    private readonly ILogger<SystemHealthMonitorService> _logger;
    private readonly object _alertSync = new();
    private readonly List<SystemHealthAlert> _recentAlerts = new();
    private readonly Dictionary<string, DateTime> _alertCooldown = new(StringComparer.Ordinal);

    private SystemHealthMonitorSnapshot? _latest;

    public SystemHealthMonitorService(
        ITelemetryDiagnosticService telemetry,
        IConnectionStatusService connectionStatus,
        IServiceScopeFactory scopeFactory,
        IDatabaseBackupService backupService,
        IOptions<SystemDiagnosticsOptions> options,
        ILogger<SystemHealthMonitorService> logger)
    {
        _telemetry = telemetry;
        _connectionStatus = connectionStatus;
        _scopeFactory = scopeFactory;
        _backupService = backupService;
        _options = options.Value;
        _logger = logger;
    }

    public event EventHandler<SystemHealthMonitorSnapshot>? StatusChanged;
    public event EventHandler<SystemHealthAlert>? AlertRaised;

    public SystemHealthMonitorSnapshot? LatestSnapshot => _latest;

    public IReadOnlyList<SystemHealthAlert> RecentAlerts
    {
        get
        {
            lock (_alertSync)
            {
                return _recentAlerts.ToList();
            }
        }
    }

    public Task<SystemHealthMonitorSnapshot> RunManualDiagnosticAsync(
        CancellationToken cancellationToken = default) =>
        EvaluateAsync(cancellationToken);

    public async Task<SystemHealthMonitorSnapshot> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var baseSnapshot = await _telemetry.RunDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        await _connectionStatus.RefreshAsync(cancellationToken).ConfigureAwait(false);

        var internetOnline = NetworkInterface.GetIsNetworkAvailable() && _connectionStatus.IsOnline;
        var diskFreeMb = Math.Round(baseSnapshot.AvailableDiskSpaceBytes / (1024d * 1024d), 1);

        var backupDir = _backupService.ResolveBackupDirectory();
        var backupFreeMb = MeasureFreeMb(backupDir);

        var queue = await LoadQueueCountsAsync(cancellationToken).ConfigureAwait(false);
        var activeQueue = queue.Pending + queue.Syncing;
        var queueWarn = Math.Max(5, _options.QueueBacklogWarnCount);
        var queueHealthy = activeQueue < queueWarn && queue.Quarantined < queueWarn;

        var warnings = new List<string>();
        if (!baseSnapshot.IsDatabaseHealthy)
        {
            warnings.Add("SQL Express is not responding.");
        }
        else if (baseSnapshot.DatabaseLatencyMs >= _options.DatabaseLatencyWarnMs)
        {
            warnings.Add($"SQL latency elevated ({baseSnapshot.DatabaseLatencyMs} ms).");
        }

        if (!baseSnapshot.IsDiskHealthy)
        {
            warnings.Add($"App disk space low ({diskFreeMb:N0} MB free).");
        }

        if (backupFreeMb >= 0 && backupFreeMb < _options.MinimumFreeDiskMegabytes)
        {
            warnings.Add($"Backup volume low ({backupFreeMb:N0} MB free under {backupDir}).");
        }

        if (!internetOnline)
        {
            warnings.Add("Internet connectivity appears offline.");
        }

        if (!baseSnapshot.IsMraHealthy || !_connectionStatus.IsMraReachable)
        {
            warnings.Add("MRA EIS API gateway is unreachable.");
        }

        if (!queueHealthy)
        {
            warnings.Add(
                $"Offline sync backlog elevated (pending {queue.Pending}, syncing {queue.Syncing}, quarantined {queue.Quarantined}).");
        }

        var overall = baseSnapshot.IsDatabaseHealthy
                      && baseSnapshot.IsDiskHealthy
                      && internetOnline
                      && (baseSnapshot.IsMraHealthy || !_connectionStatus.IsOnline)
                      && queueHealthy;

        // When offline intentionally, MRA unreachability is expected — still warn but don't fail overall solely on MRA if no internet.
        if (!internetOnline)
        {
            overall = baseSnapshot.IsDatabaseHealthy && baseSnapshot.IsDiskHealthy && queueHealthy;
        }

        var snapshot = new SystemHealthMonitorSnapshot
        {
            CheckedAtUtc = DateTime.UtcNow,
            IsDatabaseHealthy = baseSnapshot.IsDatabaseHealthy,
            DatabaseLatencyMs = baseSnapshot.DatabaseLatencyMs,
            IsInternetOnline = internetOnline,
            IsMraApiReachable = baseSnapshot.IsMraHealthy && _connectionStatus.IsMraReachable,
            MraApiStatus = baseSnapshot.MraApiStatus,
            MraPingMs = baseSnapshot.MraPingMs,
            DiskSpaceFreeMb = diskFreeMb,
            IsDiskHealthy = baseSnapshot.IsDiskHealthy,
            DiskRoot = baseSnapshot.DiskRoot,
            BackupVolumeFreeMb = backupFreeMb,
            BackupDirectory = backupDir,
            ActiveSyncQueueCount = activeQueue,
            PendingQueueCount = queue.Pending,
            SyncingQueueCount = queue.Syncing,
            QuarantinedQueueCount = queue.Quarantined,
            IsQueueHealthy = queueHealthy,
            IsPrinterHealthy = baseSnapshot.IsPrinterHealthy,
            PrinterStatus = baseSnapshot.PrinterStatus,
            OverallHealthy = overall && warnings.Count == 0,
            Summary = warnings.Count == 0
                ? "All monitored subsystems healthy."
                : string.Join(' ', warnings),
            WarningMessages = warnings
        };

        // Align overall with warning list for dashboard badges.
        snapshot = snapshot with { OverallHealthy = warnings.Count == 0 };

        _latest = snapshot;
        StatusChanged?.Invoke(this, snapshot);

        await RaiseAlertsAsync(snapshot, cancellationToken).ConfigureAwait(false);

        await _telemetry.RecordWorkerHeartbeatAsync(
                nameof(SystemHealthMonitorService),
                detail: snapshot.Summary,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return snapshot;
    }

    private async Task RaiseAlertsAsync(
        SystemHealthMonitorSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.IsDatabaseHealthy)
        {
            await EmitAlertAsync(
                    SystemHealthAlertCodes.DatabaseUnhealthy,
                    DiagnosticSeverities.Critical,
                    "SQL Express database health check failed.",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (snapshot.DatabaseLatencyMs >= _options.DatabaseLatencyWarnMs)
        {
            await EmitAlertAsync(
                    SystemHealthAlertCodes.DatabaseLatency,
                    DiagnosticSeverities.Warning,
                    $"SQL Express latency {snapshot.DatabaseLatencyMs} ms exceeds warn threshold.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!snapshot.IsDiskHealthy)
        {
            await EmitAlertAsync(
                    SystemHealthAlertCodes.DiskLow,
                    DiagnosticSeverities.Warning,
                    $"Free disk space {snapshot.DiskSpaceFreeMb:N0} MB below minimum {_options.MinimumFreeDiskMegabytes} MB.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (snapshot.BackupVolumeFreeMb >= 0
            && snapshot.BackupVolumeFreeMb < _options.MinimumFreeDiskMegabytes)
        {
            await EmitAlertAsync(
                    SystemHealthAlertCodes.BackupVolumeLow,
                    DiagnosticSeverities.Warning,
                    $"Backup volume free space {snapshot.BackupVolumeFreeMb:N0} MB is low.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!snapshot.IsInternetOnline)
        {
            await EmitAlertAsync(
                    SystemHealthAlertCodes.InternetOffline,
                    DiagnosticSeverities.Warning,
                    "Internet connectivity offline — offline queue will buffer fiscal invoices.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (snapshot.IsInternetOnline && !snapshot.IsMraApiReachable)
        {
            await EmitAlertAsync(
                    SystemHealthAlertCodes.MraUnreachable,
                    DiagnosticSeverities.Error,
                    "MRA EIS API gateway unreachable while internet is online.",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!snapshot.IsQueueHealthy)
        {
            await EmitAlertAsync(
                    SystemHealthAlertCodes.QueueBacklog,
                    DiagnosticSeverities.Warning,
                    $"Active sync queue count {snapshot.ActiveSyncQueueCount} (quarantined {snapshot.QuarantinedQueueCount}).",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task EmitAlertAsync(
        string code,
        string severity,
        string message,
        CancellationToken cancellationToken)
    {
        var cooldown = TimeSpan.FromSeconds(Math.Max(30, _options.HealthAlertCooldownSeconds));
        lock (_alertSync)
        {
            if (_alertCooldown.TryGetValue(code, out var last) && DateTime.UtcNow - last < cooldown)
            {
                return;
            }

            _alertCooldown[code] = DateTime.UtcNow;
        }

        var alert = new SystemHealthAlert
        {
            RaisedAtUtc = DateTime.UtcNow,
            Code = code,
            Severity = severity,
            Message = message
        };

        lock (_alertSync)
        {
            _recentAlerts.Insert(0, alert);
            while (_recentAlerts.Count > 40)
            {
                _recentAlerts.RemoveAt(_recentAlerts.Count - 1);
            }
        }

        _logger.LogWarning("System health alert [{Code}] {Message}", code, message);
        AlertRaised?.Invoke(this, alert);

        await _telemetry.RecordExceptionAsync(
                source: $"HealthAlert:{code}",
                exception: new InvalidOperationException(message),
                severity: severity,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(int Pending, int Syncing, int Quarantined)> LoadQueueCountsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<IOfflineInvoiceQueueRepository>();
            var counts = await queue.GetStatusCountsAsync(cancellationToken).ConfigureAwait(false);
            counts.TryGetValue(OfflineQueueStatuses.Pending, out var pending);
            counts.TryGetValue(OfflineQueueStatuses.Syncing, out var syncing);
            counts.TryGetValue(OfflineQueueStatuses.Quarantined, out var quarantined);
            return (pending, syncing, quarantined);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to load offline queue counts for health monitor.");
            return (0, 0, 0);
        }
    }

    private static double MeasureFreeMb(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root))
            {
                return -1;
            }

            var drive = new DriveInfo(root);
            return Math.Round(drive.AvailableFreeSpace / (1024d * 1024d), 1);
        }
        catch
        {
            return -1;
        }
    }
}

/// <summary>
/// Background loop for Phase 36 health monitoring (complements HealthCheckWorker with queue/backup alerts).
/// </summary>
public sealed class SystemHealthMonitorBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SystemDiagnosticsOptions _options;
    private readonly ILogger<SystemHealthMonitorBackgroundService> _logger;

    public SystemHealthMonitorBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<SystemDiagnosticsOptions> options,
        ILogger<SystemHealthMonitorBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("SystemHealthMonitorBackgroundService disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(20, _options.HealthCheckIntervalSeconds));
        _logger.LogInformation(
            "System health monitor started (interval {Seconds}s).",
            interval.TotalSeconds);

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
                // Singleton registered — resolve from root-compatible scope
                var monitor = scope.ServiceProvider.GetRequiredService<ISystemHealthMonitorService>();
                var snapshot = await monitor.EvaluateAsync(stoppingToken).ConfigureAwait(false);
                if (!snapshot.OverallHealthy)
                {
                    _logger.LogWarning("System health degraded: {Summary}", snapshot.Summary);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "System health monitor iteration failed.");
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
