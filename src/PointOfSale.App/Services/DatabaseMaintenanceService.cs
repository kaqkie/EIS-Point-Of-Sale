using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.App.Services;

public interface IDatabaseMaintenanceService
{
    bool IsMaintenanceRunning { get; }

    event EventHandler? DashboardUpdated;

    Task<DatabaseMaintenanceDashboard> GetDashboardAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatabaseMaintenanceLogEntry>> GetRecentLogsAsync(
        int take = 25,
        CancellationToken cancellationToken = default);

    Task<DatabaseMaintenanceLogEntry> RunMaintenanceAsync(
        string operation,
        bool manualTrigger = true,
        CancellationToken cancellationToken = default);
}

public sealed class DatabaseMaintenanceService : IDatabaseMaintenanceService
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IShiftManagementService _shifts;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly ITelemetryDiagnosticService _telemetry;
    private readonly DatabaseMaintenanceOptions _options;
    private readonly SystemDiagnosticsOptions _diagnosticsOptions;
    private readonly ILogger<DatabaseMaintenanceService> _logger;
    private int _running;

    public DatabaseMaintenanceService(
        ISqlConnectionFactory connectionFactory,
        IShiftManagementService shifts,
        IAuthenticationAuthorizationService auth,
        ITelemetryDiagnosticService telemetry,
        IOptions<DatabaseMaintenanceOptions> options,
        IOptions<SystemDiagnosticsOptions> diagnosticsOptions,
        ILogger<DatabaseMaintenanceService> logger)
    {
        _connectionFactory = connectionFactory;
        _shifts = shifts;
        _auth = auth;
        _telemetry = telemetry;
        _options = options.Value;
        _diagnosticsOptions = diagnosticsOptions.Value;
        _logger = logger;
    }

    public event EventHandler? DashboardUpdated;

    public bool IsMaintenanceRunning => Volatile.Read(ref _running) > 0;

    public async Task<DatabaseMaintenanceDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var sizeMb = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                """
                SELECT CAST(SUM(CAST(size AS BIGINT)) * 8 / 1024 AS BIGINT)
                FROM sys.database_files;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false) ?? 0;

        var fragmented = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                SELECT COUNT(*)
                FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, N'LIMITED') AS ips
                INNER JOIN sys.indexes AS i
                    ON ips.object_id = i.object_id AND ips.index_id = i.index_id
                WHERE ips.index_id > 0
                  AND ips.avg_fragmentation_in_percent >= @Threshold
                  AND ips.page_count >= @MinPages;
                """,
                new
                {
                    Threshold = _options.RebuildFragmentationPercentThreshold,
                    MinPages = _options.MinimumIndexPageCount
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        DateTime? lastRun = null;
        if (await TableExistsAsync(connection, "DatabaseMaintenanceLog", cancellationToken).ConfigureAwait(false))
        {
            lastRun = await connection.ExecuteScalarAsync<DateTime?>(
                new CommandDefinition(
                    """
                    SELECT TOP (1) ExecutedAtUtc
                    FROM dbo.DatabaseMaintenanceLog
                    WHERE Operation = @Operation AND Success = 1
                    ORDER BY ExecutedAtUtc DESC;
                    """,
                    new { Operation = DatabaseMaintenanceOperations.FullOptimization },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        var dashboard = new DatabaseMaintenanceDashboard
        {
            DatabaseSizeMb = sizeMb,
            FragmentedIndexesCount = fragmented,
            LastOptimizationTimestampUtc = lastRun,
            IsMaintenanceRunning = IsMaintenanceRunning,
            StatusSummary = fragmented > 0
                ? $"{fragmented} index(es) exceed {_options.RebuildFragmentationPercentThreshold}% fragmentation."
                : "Index fragmentation within acceptable limits."
        };

        return dashboard;
    }

    public async Task<IReadOnlyList<DatabaseMaintenanceLogEntry>> GetRecentLogsAsync(
        int take = 25,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await TableExistsAsync(connection, "DatabaseMaintenanceLog", cancellationToken).ConfigureAwait(false))
        {
            return Array.Empty<DatabaseMaintenanceLogEntry>();
        }

        var rows = await connection.QueryAsync<DatabaseMaintenanceLogEntry>(
            new CommandDefinition(
                """
                SELECT TOP (@Take)
                    LogId, ExecutedAtUtc, Operation, Success, Detail, DurationMs
                FROM dbo.DatabaseMaintenanceLog
                ORDER BY ExecutedAtUtc DESC, LogId DESC;
                """,
                new { Take = Math.Clamp(take, 1, 200) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    public async Task<DatabaseMaintenanceLogEntry> RunMaintenanceAsync(
        string operation,
        bool manualTrigger = true,
        CancellationToken cancellationToken = default)
    {
        if (manualTrigger)
        {
            _auth.EnsurePermission(OperatorPermissions.ManageDatabaseMaintenance);
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("Database maintenance is already running.");
        }

        var sw = Stopwatch.StartNew();
        var fragmentedBefore = 0;
        long sizeBefore = 0;
        var success = true;
        var detailBuilder = new List<string>();

        try
        {
            if (operation is DatabaseMaintenanceOperations.FullOptimization
                or DatabaseMaintenanceOperations.RebuildIndexes)
            {
                await EnsureSafeForHeavyMaintenanceAsync(cancellationToken).ConfigureAwait(false);
            }

            var dashboard = await GetDashboardAsync(cancellationToken).ConfigureAwait(false);
            fragmentedBefore = dashboard.FragmentedIndexesCount;
            sizeBefore = dashboard.DatabaseSizeMb;

            if (operation is DatabaseMaintenanceOperations.FullOptimization
                or DatabaseMaintenanceOperations.PurgeTelemetry)
            {
                var retention = Math.Max(1, _diagnosticsOptions.TelemetryRetentionDays);
                if (_options.TelemetryRetentionDays > 0)
                {
                    retention = _options.TelemetryRetentionDays;
                }

                var purged = await PurgeTelemetryAsync(retention, cancellationToken).ConfigureAwait(false);
                detailBuilder.Add($"Purged {purged} diagnostic telemetry row(s).");

                var auditPurged = await PurgeMraAuditAsync(_options.MraAuditLogRetentionDays, cancellationToken)
                    .ConfigureAwait(false);
                if (auditPurged >= 0)
                {
                    detailBuilder.Add($"Purged {auditPurged} MRA audit row(s).");
                }
            }

            if (operation is DatabaseMaintenanceOperations.FullOptimization
                or DatabaseMaintenanceOperations.UpdateStatistics)
            {
                await UpdateStatisticsAsync(cancellationToken).ConfigureAwait(false);
                detailBuilder.Add("UPDATE STATISTICS completed on user tables.");
            }

            if (operation is DatabaseMaintenanceOperations.FullOptimization
                or DatabaseMaintenanceOperations.RebuildIndexes)
            {
                var rebuilt = await RebuildFragmentedIndexesAsync(cancellationToken).ConfigureAwait(false);
                detailBuilder.Add($"Rebuilt {rebuilt} fragmented index(es).");
            }

            await _telemetry.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            success = false;
            detailBuilder.Add(ex.Message);
            await _telemetry.RecordExceptionAsync(nameof(DatabaseMaintenanceService), ex, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            _logger.LogError(ex, "Database maintenance {Operation} failed.", operation);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
            sw.Stop();
        }

        var entry = new DatabaseMaintenanceLogEntry
        {
            ExecutedAtUtc = DateTime.UtcNow,
            Operation = operation,
            Success = success,
            Detail = string.Join(" ", detailBuilder),
            DurationMs = (int)sw.ElapsedMilliseconds
        };

        await TryInsertLogAsync(entry, fragmentedBefore, sizeBefore, cancellationToken).ConfigureAwait(false);
        DashboardUpdated?.Invoke(this, EventArgs.Empty);
        return entry;
    }

    private async Task EnsureSafeForHeavyMaintenanceAsync(CancellationToken cancellationToken)
    {
        if (_options.AllowRebuildDuringOpenShift)
        {
            return;
        }

        var open = await _shifts.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false);
        if (open is not null)
        {
            throw new InvalidOperationException(
                $"Cannot rebuild indexes while shift {open.ShiftId} is open for {open.CashierName}.");
        }
    }

    private async Task<int> PurgeTelemetryAsync(int retentionDays, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await TableExistsAsync(connection, "DiagnosticTelemetryEvents", cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        if (await ProcedureExistsAsync(connection, "usp_PurgeExpiredDiagnosticTelemetry", cancellationToken)
            .ConfigureAwait(false))
        {
            return await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "EXEC dbo.usp_PurgeExpiredDiagnosticTelemetry @RetentionDays;",
                    new { RetentionDays = retentionDays },
                    commandTimeout: _options.CommandTimeoutSeconds,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM dbo.DiagnosticTelemetryEvents WHERE CreatedAtUtc < @Cutoff;",
                new { Cutoff = cutoff },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<int> PurgeMraAuditAsync(int retentionDays, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await TableExistsAsync(connection, "MraApiAuditLog", cancellationToken).ConfigureAwait(false))
        {
            return -1;
        }

        if (await ProcedureExistsAsync(connection, "usp_CleanupMraApiAuditLog", cancellationToken).ConfigureAwait(false))
        {
            return await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    "EXEC dbo.usp_CleanupMraApiAuditLog @RetentionDays;",
                    new { RetentionDays = retentionDays },
                    commandTimeout: _options.CommandTimeoutSeconds,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                "DELETE FROM dbo.MraApiAuditLog WHERE CreatedAtUtc < @Cutoff;",
                new { Cutoff = cutoff },
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task UpdateStatisticsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        const string sql = """
            DECLARE @sql nvarchar(max) = N'';
            SELECT @sql = @sql + N'UPDATE STATISTICS ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) + N' WITH FULLSCAN;' + CHAR(13)
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE t.is_ms_shipped = 0;
            EXEC sp_executesql @sql;
            """;

        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private async Task<int> RebuildFragmentedIndexesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var targets = (await connection.QueryAsync<(string SchemaName, string TableName, string IndexName)>(
            new CommandDefinition(
                """
                SELECT
                    s.name AS SchemaName,
                    o.name AS TableName,
                    i.name AS IndexName
                FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, N'LIMITED') AS ips
                INNER JOIN sys.objects AS o ON ips.object_id = o.object_id
                INNER JOIN sys.schemas AS s ON o.schema_id = s.schema_id
                INNER JOIN sys.indexes AS i ON ips.object_id = i.object_id AND ips.index_id = i.index_id
                WHERE ips.index_id > 0
                  AND ips.avg_fragmentation_in_percent >= @Threshold
                  AND ips.page_count >= @MinPages
                  AND o.is_ms_shipped = 0;
                """,
                new
                {
                    Threshold = _options.RebuildFragmentationPercentThreshold,
                    MinPages = _options.MinimumIndexPageCount
                },
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var rebuilt = 0;
        foreach (var (schema, table, index) in targets)
        {
            var sql = $"ALTER INDEX {Quote(index)} ON {Quote(schema)}.{Quote(table)} REBUILD;";
            await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    commandTimeout: _options.CommandTimeoutSeconds,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            rebuilt++;
            _logger.LogInformation("Rebuilt index {Index} on {Schema}.{Table}.", index, schema, table);
        }

        return rebuilt;
    }

    private async Task TryInsertLogAsync(
        DatabaseMaintenanceLogEntry entry,
        int fragmentedBefore,
        long sizeBefore,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await TableExistsAsync(connection, "DatabaseMaintenanceLog", cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            entry.LogId = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    """
                    INSERT INTO dbo.DatabaseMaintenanceLog
                        (ExecutedAtUtc, Operation, Success, Detail, DurationMs, FragmentedIndexesBefore, DatabaseSizeMbBefore)
                    OUTPUT INSERTED.LogId
                    VALUES
                        (@ExecutedAtUtc, @Operation, @Success, @Detail, @DurationMs, @FragmentedBefore, @SizeBefore);
                    """,
                    new
                    {
                        entry.ExecutedAtUtc,
                        entry.Operation,
                        entry.Success,
                        entry.Detail,
                        entry.DurationMs,
                        FragmentedBefore = fragmentedBefore,
                        SizeBefore = sizeBefore
                    },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist database maintenance log row.");
        }
    }

    private static string Quote(string identifier) => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static async Task<bool> TableExistsAsync(
        System.Data.Common.DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM sys.tables WHERE name = @Name;",
                new { Name = tableName },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }

    private static async Task<bool> ProcedureExistsAsync(
        System.Data.Common.DbConnection connection,
        string procedureName,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                """
                SELECT COUNT(*)
                FROM sys.procedures
                WHERE name = @Name;
                """,
                new { Name = procedureName },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }
}

public sealed class DatabaseMaintenanceBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DatabaseMaintenanceOptions _options;
    private readonly ILogger<DatabaseMaintenanceBackgroundService> _logger;

    public DatabaseMaintenanceBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<DatabaseMaintenanceOptions> options,
        ILogger<DatabaseMaintenanceBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("DatabaseMaintenanceBackgroundService is disabled.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _options.MaintenanceIntervalHours));
        _logger.LogInformation(
            "DatabaseMaintenanceBackgroundService started (interval {Hours}h).",
            interval.TotalHours);

        try
        {
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken).ConfigureAwait(false);
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
                var maintenance = scope.ServiceProvider.GetRequiredService<IDatabaseMaintenanceService>();
                await maintenance.RunMaintenanceAsync(
                        DatabaseMaintenanceOperations.FullOptimization,
                        manualTrigger: false,
                        stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("shift", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Skipping scheduled database maintenance: {Message}", ex.Message);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled database maintenance failed.");
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
