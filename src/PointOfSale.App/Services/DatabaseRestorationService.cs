using System.IO;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Repositories;
using IOPath = System.IO.Path;

namespace PointOfSale.App.Services;

public interface IDatabaseRestorationService
{
    bool IsRestoring { get; }

    Task<DatabaseRestoreResult> VerifyBackupAsync(string backupFilePath, CancellationToken cancellationToken = default);

    Task<DatabaseRestoreResult> RestoreAsync(
        string backupFilePath,
        bool confirmDestructive = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Validates backup checksums / RESTORE VERIFYONLY, preserves unsynced offline invoices,
/// then performs a controlled SQL Express RESTORE DATABASE recovery.
/// </summary>
public sealed class DatabaseRestorationService : IDatabaseRestorationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HashSet<string> PreserveStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        OfflineQueueStatuses.Pending,
        OfflineQueueStatuses.Syncing,
        OfflineQueueStatuses.Quarantined
    };

    private readonly string _connectionString;
    private readonly DatabaseBackupOptions _options;
    private readonly IDatabaseBackupService _backupService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDatabaseBootstrapService _bootstrapService;
    private readonly ILogger<DatabaseRestorationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _isRestoring;

    public DatabaseRestorationService(
        IConfiguration configuration,
        IOptions<DatabaseBackupOptions> options,
        IDatabaseBackupService backupService,
        IServiceScopeFactory scopeFactory,
        IDatabaseBootstrapService bootstrapService,
        ILogger<DatabaseRestorationService> logger)
    {
        _connectionString = configuration.GetConnectionString("PosDatabase")
            ?? throw new InvalidOperationException("Connection string 'PosDatabase' is missing.");
        _options = options.Value;
        _backupService = backupService;
        _scopeFactory = scopeFactory;
        _bootstrapService = bootstrapService;
        _logger = logger;
    }

    public bool IsRestoring => _isRestoring;

    public async Task<DatabaseRestoreResult> VerifyBackupAsync(
        string backupFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupFilePath) || !File.Exists(backupFilePath))
        {
            return DatabaseRestoreResult.Failed("Backup file was not found.");
        }

        var manifest = await TryLoadManifestAsync(backupFilePath, cancellationToken).ConfigureAwait(false);
        var checksumOk = manifest is null
            || DatabaseBackupService.VerifyChecksum(backupFilePath, manifest.Sha256Checksum);

        if (manifest is not null && !checksumOk)
        {
            return new DatabaseRestoreResult
            {
                Success = false,
                ChecksumVerified = false,
                Error = "Backup SHA-256 checksum does not match the manifest. File may be corrupt."
            };
        }

        var verifyOk = await RunVerifyOnlyAsync(backupFilePath, cancellationToken).ConfigureAwait(false);
        if (!verifyOk)
        {
            return new DatabaseRestoreResult
            {
                Success = false,
                ChecksumVerified = checksumOk,
                VerifyOnlyPassed = false,
                Error = "RESTORE VERIFYONLY failed. The .bak media is not restorable."
            };
        }

        var schemaNote = manifest is null
            ? "No manifest found; VERIFYONLY passed."
            : $"Manifest schema v{manifest.SchemaVersion}; checksum OK.";

        return new DatabaseRestoreResult
        {
            Success = true,
            ChecksumVerified = checksumOk || manifest is null,
            VerifyOnlyPassed = true,
            Message = schemaNote
        };
    }

    public async Task<DatabaseRestoreResult> RestoreAsync(
        string backupFilePath,
        bool confirmDestructive = false,
        CancellationToken cancellationToken = default)
    {
        if (!confirmDestructive)
        {
            return DatabaseRestoreResult.Failed(
                "Restore requires explicit confirmation (destructive overwrite of the local database).");
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return DatabaseRestoreResult.Failed("A restore is already in progress.");
        }

        _isRestoring = true;
        try
        {
            var verification = await VerifyBackupAsync(backupFilePath, cancellationToken).ConfigureAwait(false);
            if (!verification.Success)
            {
                return verification;
            }

            var builder = new SqlConnectionStringBuilder(_connectionString);
            var databaseName = builder.InitialCatalog;
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                return DatabaseRestoreResult.Failed("PosDatabase connection string must include Database/Initial Catalog.");
            }

            // Preserve unsynced fiscal work before the restore overwrites the DB.
            var preservePath = IOPath.Combine(
                _backupService.ResolveBackupDirectory(),
                $"offline-queue-preserve_{DateTime.Now:yyyyMMdd_HHmmss}.json");
            Directory.CreateDirectory(IOPath.GetDirectoryName(preservePath)!);

            var preserved = await ExportUnsyncedQueueAsync(databaseName, preservePath, cancellationToken)
                .ConfigureAwait(false);

            await PerformRestoreAsync(databaseName, backupFilePath, cancellationToken).ConfigureAwait(false);

            // Re-apply schema migrations for the restored snapshot if it is older than target.
            await _bootstrapService.EnsureDatabaseReadyAsync(cancellationToken).ConfigureAwait(false);

            var reimported = await ImportPreservedQueueAsync(preservePath, cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                "Database restore completed from {Bak}. Preserved {Preserved} unsynced invoices; reimported {Reimported}.",
                backupFilePath,
                preserved,
                reimported);

            return new DatabaseRestoreResult
            {
                Success = true,
                ChecksumVerified = verification.ChecksumVerified,
                VerifyOnlyPassed = verification.VerifyOnlyPassed,
                PreservedQueueItems = preserved,
                RestoredQueueItems = reimported,
                Message =
                    $"Restore succeeded. Preserved {preserved} unsynced invoice(s); re-queued {reimported}. " +
                    $"Preservation file: {preservePath}"
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Database restore failed.");
            return DatabaseRestoreResult.Failed(ex.Message);
        }
        finally
        {
            _isRestoring = false;
            _gate.Release();
        }
    }

    private async Task<int> ExportUnsyncedQueueAsync(
        string databaseName,
        string preservePath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var connections = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();
            await using var connection = await connections.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            var invoices = (await connection.QueryAsync<PreservedOfflineInvoice>(
                    new CommandDefinition(
                        """
                        SELECT
                            Id AS OriginalId,
                            PayloadJson,
                            CreatedAt,
                            Status,
                            RetryCount,
                            NextRetryTime,
                            ErrorMessage,
                            FiscalResponseJson
                        FROM dbo.OfflineInvoiceQueue
                        WHERE Status IN @Statuses
                        ORDER BY CreatedAt ASC, Id ASC;
                        """,
                        new { Statuses = PreserveStatuses.ToArray() },
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false)).AsList();

            var bundle = new OfflineQueuePreservationBundle
            {
                CapturedAtUtc = DateTime.UtcNow,
                SourceDatabase = databaseName,
                Invoices = invoices
            };

            await File.WriteAllTextAsync(
                    preservePath,
                    JsonSerializer.Serialize(bundle, JsonOptions),
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);

            return invoices.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to export offline queue before restore; continuing with empty preservation.");
            await File.WriteAllTextAsync(
                    preservePath,
                    JsonSerializer.Serialize(
                        new OfflineQueuePreservationBundle
                        {
                            CapturedAtUtc = DateTime.UtcNow,
                            SourceDatabase = databaseName,
                            Invoices = Array.Empty<PreservedOfflineInvoice>()
                        },
                        JsonOptions),
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);
            return 0;
        }
    }

    private async Task<int> ImportPreservedQueueAsync(string preservePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(preservePath))
        {
            return 0;
        }

        var json = await File.ReadAllTextAsync(preservePath, cancellationToken).ConfigureAwait(false);
        var bundle = JsonSerializer.Deserialize<OfflineQueuePreservationBundle>(json, JsonOptions);
        if (bundle?.Invoices is not { Count: > 0 })
        {
            return 0;
        }

        using var scope = _scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IOfflineInvoiceQueueRepository>();
        var connections = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        await using var connection = await connections.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var existingPayloads = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT PayloadJson FROM dbo.OfflineInvoiceQueue;",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var imported = 0;
        foreach (var invoice in bundle.Invoices)
        {
            if (string.IsNullOrWhiteSpace(invoice.PayloadJson) || existingPayloads.Contains(invoice.PayloadJson))
            {
                continue;
            }

            // Always re-queue as PENDING so FIFO sync can retry after disaster recovery.
            await queue.EnqueuePendingAsync(invoice.PayloadJson, cancellationToken).ConfigureAwait(false);
            existingPayloads.Add(invoice.PayloadJson);
            imported++;
        }

        return imported;
    }

    private async Task PerformRestoreAsync(string databaseName, string bakPath, CancellationToken cancellationToken)
    {
        var masterBuilder = new SqlConnectionStringBuilder(_connectionString)
        {
            InitialCatalog = "master"
        };

        await using var connection = new SqlConnection(masterBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var timeout = Math.Clamp(_options.CommandTimeoutSeconds, 60, 3600);
        var escaped = EscapeIdent(databaseName);

        // Kick sessions and restore with REPLACE.
        await using (var prep = connection.CreateCommand())
        {
            prep.CommandTimeout = timeout;
            prep.CommandText = $"""
                IF DB_ID(N'{escaped}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{escaped}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                END
                """;
            await prep.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var restore = connection.CreateCommand())
        {
            restore.CommandTimeout = timeout;
            restore.CommandText = $"""
                RESTORE DATABASE [{escaped}]
                FROM DISK = @BakPath
                WITH REPLACE, RECOVERY, STATS = 10;
                """;
            restore.Parameters.Add(new SqlParameter("@BakPath", bakPath));
            await restore.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var multi = connection.CreateCommand())
        {
            multi.CommandTimeout = timeout;
            multi.CommandText = $"ALTER DATABASE [{escaped}] SET MULTI_USER;";
            await multi.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> RunVerifyOnlyAsync(string bakPath, CancellationToken cancellationToken)
    {
        var masterBuilder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(masterBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var checksumClause = _options.UseChecksum ? ", CHECKSUM" : string.Empty;
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Clamp(_options.CommandTimeoutSeconds, 60, 3600);
        command.CommandText = $"""
            RESTORE VERIFYONLY
            FROM DISK = @BakPath
            WITH LOADHISTORY{checksumClause};
            """;
        command.Parameters.Add(new SqlParameter("@BakPath", bakPath));

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "RESTORE VERIFYONLY failed for {Path}.", bakPath);
            return false;
        }
    }

    private static async Task<DatabaseBackupManifest?> TryLoadManifestAsync(
        string backupFilePath,
        CancellationToken cancellationToken)
    {
        var manifestPath = IOPath.ChangeExtension(backupFilePath, ".manifest.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<DatabaseBackupManifest>(json, JsonOptions);
    }

    private static string EscapeIdent(string value) => value.Replace("]", "]]", StringComparison.Ordinal)
        .Replace("'", "''", StringComparison.Ordinal);
}
