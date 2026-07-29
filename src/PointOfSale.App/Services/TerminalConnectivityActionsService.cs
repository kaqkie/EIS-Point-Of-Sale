using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;

namespace PointOfSale.App.Services;

/// <summary>
/// Shared cashier/admin actions: MRA EIS ping, terminal update check, and light API sync verification.
/// </summary>
public interface ITerminalConnectivityActionsService
{
    Task<TerminalPingActionResult> PingMraAsync(CancellationToken cancellationToken = default);

    Task<TerminalUpdateActionResult> CheckTerminalUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ping EIS, refresh connection status, sync latest configs when JWT is available,
    /// and report offline-queue backlog.
    /// </summary>
    Task<TerminalApiSyncActionResult> VerifyAndSyncApisAsync(CancellationToken cancellationToken = default);
}

public sealed class TerminalConnectivityActionsService : ITerminalConnectivityActionsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConnectionStatusService _connectionStatus;
    private readonly IApplicationUpdateService _updateService;
    private readonly ILogger<TerminalConnectivityActionsService> _logger;

    public TerminalConnectivityActionsService(
        IServiceScopeFactory scopeFactory,
        IConnectionStatusService connectionStatus,
        IApplicationUpdateService updateService,
        ILogger<TerminalConnectivityActionsService> logger)
    {
        _scopeFactory = scopeFactory;
        _connectionStatus = connectionStatus;
        _updateService = updateService;
        _logger = logger;
    }

    public async Task<TerminalPingActionResult> PingMraAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var ping = scope.ServiceProvider.GetRequiredService<MraEisPingService>();
        var result = await ping.PingAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _connectionStatus.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Connection refresh after ping failed.");
        }

        if (!result.Attempted)
        {
            return TerminalPingActionResult.Failed(
                "MRA ping skipped — activate the terminal (JWT) first, then retry.",
                result);
        }

        if (result.Success)
        {
            var server = result.ServerDate is { } dt
                ? $" Server date: {dt.ToLocalTime():yyyy-MM-dd HH:mm:ss}."
                : string.Empty;
            return TerminalPingActionResult.Succeeded(
                $"MRA ping successful ({result.ElapsedMs ?? 0} ms).{server}",
                result);
        }

        if (result.Reachable)
        {
            return TerminalPingActionResult.Failed(
                $"MRA host reached but ping was not successful ({result.ElapsedMs ?? 0} ms): {result.Detail ?? "rejected"}.",
                result);
        }

        return TerminalPingActionResult.Failed(
            $"MRA ping failed ({result.ElapsedMs ?? 0} ms): {result.Detail ?? "unreachable"}.",
            result);
    }

    public async Task<TerminalUpdateActionResult> CheckTerminalUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        var current = _updateService.CurrentVersion;
        var result = await _updateService.CheckForUpdatesAsync(cancellationToken).ConfigureAwait(false);

        if (!result.Enabled)
        {
            return TerminalUpdateActionResult.Info(
                $"Terminal update checks are disabled (current v{current}). Enable ApplicationUpdate in settings to use the feed.",
                result);
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return TerminalUpdateActionResult.Failed(
                $"Update check failed (current v{current}): {result.Error}",
                result);
        }

        if (result.UpdateAvailable || result.Staged)
        {
            var notes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
                ? string.Empty
                : $" Notes: {Truncate(result.ReleaseNotes, 120)}";
            var staged = result.Staged ? " Package staged — restart to apply." : string.Empty;
            var mandatory = result.Mandatory ? " (mandatory)" : string.Empty;
            return TerminalUpdateActionResult.Available(
                $"Update available{mandatory}: v{current} → v{result.AvailableVersion}.{staged}{notes}",
                result);
        }

        return TerminalUpdateActionResult.Succeeded(
            $"Terminal is up to date (v{current}).",
            result);
    }

    public async Task<TerminalApiSyncActionResult> VerifyAndSyncApisAsync(
        CancellationToken cancellationToken = default)
    {
        var ping = await PingMraAsync(cancellationToken).ConfigureAwait(false);
        string? configRemark = null;
        var configOk = false;
        var pending = 0;
        var syncing = 0;
        var quarantined = 0;

        using var scope = _scopeFactory.CreateScope();
        try
        {
            var onboarding = scope.ServiceProvider.GetService<TerminalOnboardingService>();
            if (onboarding is not null)
            {
                var configs = await onboarding.GetLatestConfigsAsync(cancellationToken).ConfigureAwait(false);
                configOk = configs.IsUsable;
                configRemark = configs.Success
                    ? "Latest MRA configs synced."
                    : configs.UsedLocalFallback
                        ? $"Local config fallback active ({configs.Remark ?? "cached"})."
                        : $"Config sync: {configs.Remark ?? "failed"}";
            }
            else
            {
                configRemark = "Config sync unavailable.";
            }
        }
        catch (Exception ex)
        {
            configRemark = $"Config sync error: {ex.Message}";
            _logger.LogWarning(ex, "VerifyAndSyncApis config sync failed.");
        }

        string? repairRemark = null;
        try
        {
            var offlineSales = scope.ServiceProvider.GetService<OfflineSalesQueueService>();
            if (offlineSales is not null)
            {
                var repair = await offlineSales
                    .RepairAllReceiptIdentifiersAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (repair.Rewritten > 0 || repair.Failed > 0 || repair.TransientQuarantinesReleased > 0
                    || repair.AgedReceiptsArchived > 0)
                {
                    repairRemark = repair.SummaryMessage;
                }
            }
        }
        catch (Exception ex)
        {
            repairRemark = $"Receipt ID repair error: {ex.Message}";
            _logger.LogWarning(ex, "VerifyAndSyncApis receipt ID repair failed.");
        }

        try
        {
            var queue = scope.ServiceProvider.GetService<IOfflineInvoiceQueueRepository>();
            if (queue is not null)
            {
                var counts = await queue.GetStatusCountsAsync(cancellationToken).ConfigureAwait(false);
                pending = counts.GetValueOrDefault(OfflineQueueStatuses.Pending);
                syncing = counts.GetValueOrDefault(OfflineQueueStatuses.Syncing);
                quarantined = counts.GetValueOrDefault(OfflineQueueStatuses.Quarantined);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to read offline queue counts during API verify.");
        }

        var overallOk = ping.Success && configOk;
        var message =
            $"{ping.Message} {configRemark} " +
            (string.IsNullOrWhiteSpace(repairRemark) ? string.Empty : $"{repairRemark} ") +
            $"Offline queue — pending: {pending}, syncing: {syncing}, quarantined: {quarantined}. " +
            $"Connection: {_connectionStatus.StatusText}";

        return new TerminalApiSyncActionResult(
            overallOk,
            message.Trim(),
            ping,
            configOk,
            configRemark,
            pending,
            syncing,
            quarantined);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

public sealed record TerminalPingActionResult(
    bool Success,
    string Message,
    MraPingResult? Ping)
{
    public static TerminalPingActionResult Succeeded(string message, MraPingResult ping) =>
        new(true, message, ping);

    public static TerminalPingActionResult Failed(string message, MraPingResult? ping = null) =>
        new(false, message, ping);
}

public sealed record TerminalUpdateActionResult(
    bool Success,
    bool UpdateAvailable,
    string Message,
    UpdateCheckResult? Check)
{
    public static TerminalUpdateActionResult Succeeded(string message, UpdateCheckResult check) =>
        new(true, false, message, check);

    public static TerminalUpdateActionResult Available(string message, UpdateCheckResult check) =>
        new(true, true, message, check);

    public static TerminalUpdateActionResult Info(string message, UpdateCheckResult check) =>
        new(true, false, message, check);

    public static TerminalUpdateActionResult Failed(string message, UpdateCheckResult? check = null) =>
        new(false, false, message, check);
}

public sealed record TerminalApiSyncActionResult(
    bool Success,
    string Message,
    TerminalPingActionResult Ping,
    bool ConfigSynced,
    string? ConfigRemark,
    int PendingQueueCount,
    int SyncingQueueCount,
    int QuarantinedQueueCount);
