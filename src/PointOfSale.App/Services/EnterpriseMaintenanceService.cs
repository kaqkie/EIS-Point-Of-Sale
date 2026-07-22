using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Services;

namespace PointOfSale.App.Services;

public interface IEnterpriseMaintenanceService
{
    Task<EnterpriseMaintenanceResult> ExecuteCommandAsync(
        string commandType,
        bool supervisorOverride = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnterpriseMaintenanceResult>> GetRecentResultsAsync();
}

public sealed class EnterpriseMaintenanceResult
{
    public required string CommandType { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime ExecutedAtUtc { get; init; } = DateTime.UtcNow;
    public long DurationMs { get; init; }
}

public sealed class EnterpriseMaintenanceService : IEnterpriseMaintenanceService
{
    private readonly IShiftManagementService _shifts;
    private readonly IPerformanceProfilingService _profiling;
    private readonly ITelemetryDiagnosticService _telemetry;
    private readonly TerminalOnboardingService _onboarding;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly EnterpriseMaintenanceOptions _options;
    private readonly ILogger<EnterpriseMaintenanceService> _logger;
    private readonly List<EnterpriseMaintenanceResult> _recent = new();
    private readonly object _recentLock = new();

    public EnterpriseMaintenanceService(
        IShiftManagementService shifts,
        IPerformanceProfilingService profiling,
        ITelemetryDiagnosticService telemetry,
        TerminalOnboardingService onboarding,
        ISqlConnectionFactory connectionFactory,
        IAuthenticationAuthorizationService auth,
        IOptions<EnterpriseMaintenanceOptions> options,
        ILogger<EnterpriseMaintenanceService> logger)
    {
        _shifts = shifts;
        _profiling = profiling;
        _telemetry = telemetry;
        _onboarding = onboarding;
        _connectionFactory = connectionFactory;
        _auth = auth;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<EnterpriseMaintenanceResult> ExecuteCommandAsync(
        string commandType,
        bool supervisorOverride = false,
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.ExecuteEnterpriseMaintenance);

        if (_options.RequireSupervisorForIndexMaintenance
            && commandType == EnterpriseMaintenanceCommandTypes.ReorganizeIndexes
            && !supervisorOverride
            && !_auth.HasPermission(OperatorPermissions.ManageUsers))
        {
            throw new InvalidOperationException("Supervisor authorization required for index maintenance.");
        }

        await EnsureSafeForMaintenanceAsync(commandType, cancellationToken).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        EnterpriseMaintenanceResult result;
        try
        {
            result = commandType switch
            {
                EnterpriseMaintenanceCommandTypes.ClearCaches => await ClearCachesAsync(sw, cancellationToken)
                    .ConfigureAwait(false),
                EnterpriseMaintenanceCommandTypes.ReorganizeIndexes => await ReorganizeIndexesAsync(sw, cancellationToken)
                    .ConfigureAwait(false),
                EnterpriseMaintenanceCommandTypes.RenewMraCredentials => await RenewCredentialsAsync(sw, cancellationToken)
                    .ConfigureAwait(false),
                EnterpriseMaintenanceCommandTypes.FlushTelemetry => await FlushTelemetryAsync(sw, cancellationToken)
                    .ConfigureAwait(false),
                _ => new EnterpriseMaintenanceResult
                {
                    CommandType = commandType,
                    Success = false,
                    Message = "Unknown maintenance command.",
                    DurationMs = sw.ElapsedMilliseconds
                }
            };
        }
        catch (Exception ex)
        {
            result = new EnterpriseMaintenanceResult
            {
                CommandType = commandType,
                Success = false,
                Message = ex.Message,
                DurationMs = sw.ElapsedMilliseconds
            };
            await _telemetry.RecordExceptionAsync(nameof(EnterpriseMaintenanceService), ex, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        Remember(result);
        _logger.LogInformation(
            "Enterprise maintenance {Command} completed success={Success} in {Ms} ms.",
            commandType,
            result.Success,
            result.DurationMs);

        return result;
    }

    public Task<IReadOnlyList<EnterpriseMaintenanceResult>> GetRecentResultsAsync()
    {
        lock (_recentLock)
        {
            return Task.FromResult<IReadOnlyList<EnterpriseMaintenanceResult>>(_recent.ToList());
        }
    }

    private async Task EnsureSafeForMaintenanceAsync(string commandType, CancellationToken cancellationToken)
    {
        if (_options.AllowMaintenanceDuringOpenShift)
        {
            return;
        }

        if (commandType is not (EnterpriseMaintenanceCommandTypes.ReorganizeIndexes
            or EnterpriseMaintenanceCommandTypes.RenewMraCredentials))
        {
            return;
        }

        var open = await _shifts.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false);
        if (open is not null)
        {
            throw new InvalidOperationException(
                $"Cannot run {commandType} while shift {open.ShiftId} is open for {open.CashierName}.");
        }
    }

    private async Task<EnterpriseMaintenanceResult> ClearCachesAsync(
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
        GC.WaitForPendingFinalizers();
        await _telemetry.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
        await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);

        return new EnterpriseMaintenanceResult
        {
            CommandType = EnterpriseMaintenanceCommandTypes.ClearCaches,
            Success = true,
            Message = "Application caches cleared and expired diagnostic rows purged.",
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private async Task<EnterpriseMaintenanceResult> ReorganizeIndexesAsync(
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        const string sql = """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql = @sql + N'ALTER INDEX ALL ON ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' REORGANIZE;' + CHAR(13)
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE t.name IN (
                N'OfflineInvoiceQueue',
                N'FinancialClosures',
                N'DiagnosticTelemetryEvents',
                N'HeadOfficeSyncOutbox',
                N'LocalInventory'
            );
            EXEC sp_executesql @sql;
            """;

        await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    commandTimeout: _options.IndexReorganizeCommandTimeoutSeconds,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return new EnterpriseMaintenanceResult
        {
            CommandType = EnterpriseMaintenanceCommandTypes.ReorganizeIndexes,
            Success = true,
            Message = "SQL Express index reorganization completed on operational tables.",
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private async Task<EnterpriseMaintenanceResult> RenewCredentialsAsync(
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        var configs = await _onboarding.GetLatestConfigsAsync(cancellationToken).ConfigureAwait(false);
        if (!configs.Success)
        {
            return new EnterpriseMaintenanceResult
            {
                CommandType = EnterpriseMaintenanceCommandTypes.RenewMraCredentials,
                Success = false,
                Message = configs.Remark ?? "MRA configuration refresh failed.",
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        return new EnterpriseMaintenanceResult
        {
            CommandType = EnterpriseMaintenanceCommandTypes.RenewMraCredentials,
            Success = true,
            Message = "MRA terminal credentials and configuration cache refreshed.",
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private async Task<EnterpriseMaintenanceResult> FlushTelemetryAsync(
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        await CaptureSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var flushed = await _profiling.FlushMetricsToCorporateEndpointAsync(cancellationToken).ConfigureAwait(false);
        return new EnterpriseMaintenanceResult
        {
            CommandType = EnterpriseMaintenanceCommandTypes.FlushTelemetry,
            Success = flushed,
            Message = flushed
                ? "Performance metrics flushed to corporate telemetry."
                : "Telemetry flush skipped or endpoint unavailable.",
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private Task CaptureSnapshotAsync(CancellationToken cancellationToken) =>
        _profiling.CaptureSnapshotAsync(cancellationToken);

    private void Remember(EnterpriseMaintenanceResult result)
    {
        lock (_recentLock)
        {
            _recent.Insert(0, result);
            if (_recent.Count > 20)
            {
                _recent.RemoveAt(_recent.Count - 1);
            }
        }
    }
}
