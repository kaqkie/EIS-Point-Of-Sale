using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Security;
using IOPath = System.IO.Path;

namespace PointOfSale.App.Services;

public interface IDatabaseBackupService
{
    event EventHandler? StatusChanged;

    bool IsBackingUp { get; }
    /// <summary>Phase 33 alias for <see cref="IsBackingUp"/>.</summary>
    bool IsBackupInProgress { get; }
    DateTime? LastBackupTime { get; }
    /// <summary>Phase 33 alias for <see cref="LastBackupTime"/>.</summary>
    DateTime? LastBackupTimestamp { get; }
    string? BackupFilePath { get; }
    /// <summary>Phase 33 alias for <see cref="BackupFilePath"/>.</summary>
    string? BackupFileLocation { get; }
    string? LastError { get; }

    string ResolveBackupDirectory();
    double GetBackupStorageUsageMb();
    Task<DatabaseBackupStatusSnapshot> GetStatusSnapshotAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DatabaseBackupHistoryEntry>> GetHistoryAsync(int take = 30, CancellationToken cancellationToken = default);
    Task<DatabaseBackupResult> BackupNowAsync(string trigger = DatabaseBackupTriggers.Manual, CancellationToken cancellationToken = default);
    Task<DatabaseBackupResult> BackupOnShiftCloseAsync(CancellationToken cancellationToken = default);
    Task<DatabaseBackupResult> BackupOnEndOfDayAsync(CancellationToken cancellationToken = default);
}

public sealed class DatabaseBackupStatusSnapshot
{
    public bool IsBackupInProgress { get; init; }
    public DateTime? LastBackupTimestamp { get; init; }
    public string? BackupFileLocation { get; init; }
    public string BackupDirectory { get; init; } = string.Empty;
    public double StorageUsageMb { get; init; }
    public string? LastError { get; init; }
    public int HistoryCount { get; init; }
}

/// <summary>
/// Automated SQL Express BACKUP DATABASE snapshots with rolling retention,
/// SHA-256 manifests, and DPAPI-encrypted sensitive configuration sidecars.
/// </summary>
public sealed class DatabaseBackupService : IDatabaseBackupService
{
    public const string LastMidnightBackupConfigKey = "Backup.LastMidnightLocalDate";
    public const string LastShiftBackupConfigKey = "Backup.LastShiftBackupUtc";
    public const string LastEndOfDayBackupConfigKey = "Backup.LastEndOfDayLocalDate";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly DatabaseBackupOptions _options;
    private readonly DatabaseBootstrapOptions _bootstrapOptions;
    private readonly string _connectionString;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DatabaseBackupService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _isBackingUp;
    private DateTime? _lastBackupTime;
    private string? _backupFilePath;
    private string? _lastError;

    public DatabaseBackupService(
        IConfiguration configuration,
        IOptions<DatabaseBackupOptions> options,
        IOptions<DatabaseBootstrapOptions> bootstrapOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<DatabaseBackupService> logger)
    {
        _connectionString = configuration.GetConnectionString("PosDatabase")
            ?? throw new InvalidOperationException("Connection string 'PosDatabase' is missing.");
        _options = options.Value;
        _bootstrapOptions = bootstrapOptions.Value;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public event EventHandler? StatusChanged;

    public bool IsBackingUp => _isBackingUp;
    public bool IsBackupInProgress => _isBackingUp;
    public DateTime? LastBackupTime => _lastBackupTime;
    public DateTime? LastBackupTimestamp => _lastBackupTime;
    public string? BackupFilePath => _backupFilePath;
    public string? BackupFileLocation => _backupFilePath;
    public string? LastError => _lastError;

    public string ResolveBackupDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.BackupDirectory))
        {
            return Environment.ExpandEnvironmentVariables(_options.BackupDirectory.Trim());
        }

        return IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AlbertRetailTerminal",
            "Backups");
    }

    public async Task<IReadOnlyList<DatabaseBackupHistoryEntry>> GetHistoryAsync(
        int take = 30,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        const string sql = """
            SELECT TOP (@Take)
                BackupId, CreatedAtUtc, Trigger, BackupFilePath, Sha256Checksum,
                BackupBytes, Success, ErrorMessage
            FROM dbo.DatabaseBackupHistory
            ORDER BY CreatedAtUtc DESC, BackupId DESC;
            """;

        try
        {
            await using var connection = await connections.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var rows = await connection.QueryAsync<DatabaseBackupHistoryEntry>(
                new CommandDefinition(sql, new { Take = take }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return rows.AsList();
        }
        catch (SqlException ex) when (ex.Number is 208 or 207)
        {
            // Table not yet bootstrapped — fall back to filesystem manifests.
            return LoadHistoryFromFilesystem(take);
        }
    }

    public Task<DatabaseBackupResult> BackupOnShiftCloseAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !_options.BackupOnShiftClose)
        {
            return Task.FromResult(DatabaseBackupResult.Failed("End-of-shift backup is disabled."));
        }

        return BackupNowAsync(DatabaseBackupTriggers.EndOfShift, cancellationToken);
    }

    public Task<DatabaseBackupResult> BackupOnEndOfDayAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || !_options.BackupOnEndOfDay)
        {
            return Task.FromResult(DatabaseBackupResult.Failed("End-of-day backup is disabled."));
        }

        return BackupNowAsync(DatabaseBackupTriggers.EndOfDay, cancellationToken);
    }

    public double GetBackupStorageUsageMb()
    {
        var directory = ResolveBackupDirectory();
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch
            {
                // Ignore locked/transient files.
            }
        }

        return Math.Round(bytes / (1024d * 1024d), 2);
    }

    public async Task<DatabaseBackupStatusSnapshot> GetStatusSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var history = await GetHistoryAsync(5, cancellationToken).ConfigureAwait(false);
        var latest = history.FirstOrDefault(h => h.Success);
        if (latest is not null && _lastBackupTime is null)
        {
            _lastBackupTime = latest.CreatedAtUtc;
            _backupFilePath = latest.BackupFilePath;
        }

        return new DatabaseBackupStatusSnapshot
        {
            IsBackupInProgress = _isBackingUp,
            LastBackupTimestamp = _lastBackupTime,
            BackupFileLocation = _backupFilePath,
            BackupDirectory = ResolveBackupDirectory(),
            StorageUsageMb = GetBackupStorageUsageMb(),
            LastError = _lastError,
            HistoryCount = history.Count
        };
    }

    public async Task<DatabaseBackupResult> BackupNowAsync(
        string trigger = DatabaseBackupTriggers.Manual,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled && trigger != DatabaseBackupTriggers.Manual)
        {
            return DatabaseBackupResult.Failed("Database backup is disabled.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return DatabaseBackupResult.Failed("A backup is already in progress.");
        }

        _isBackingUp = true;
        _lastError = null;
        RaiseStatusChanged();

        var directory = ResolveBackupDirectory();
        Directory.CreateDirectory(directory);

        var builder = new SqlConnectionStringBuilder(_connectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            _isBackingUp = false;
            _gate.Release();
            return DatabaseBackupResult.Failed("PosDatabase connection string must include Database/Initial Catalog.");
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        var bakName = $"PointOfSale_{stamp}_{trigger}.bak";
        var bakPath = IOPath.Combine(directory, bakName);
        var manifestPath = IOPath.ChangeExtension(bakPath, ".manifest.json");
        var secretsPath = IOPath.ChangeExtension(bakPath, ".secrets.dpapi.json");

        try
        {
            var compressed = await ExecuteBackupAsync(databaseName, bakPath, cancellationToken).ConfigureAwait(false);
            var checksum = await ComputeSha256HexAsync(bakPath, cancellationToken).ConfigureAwait(false);
            var fileInfo = new FileInfo(bakPath);

            var secretsWritten = await WriteEncryptedSecretsSidecarAsync(secretsPath, cancellationToken)
                .ConfigureAwait(false);

            var manifest = new DatabaseBackupManifest
            {
                DatabaseName = databaseName,
                BackupFileName = bakName,
                BackupFilePath = bakPath,
                ManifestFilePath = manifestPath,
                SecretsSidecarPath = secretsWritten ? secretsPath : null,
                Sha256Checksum = checksum,
                BackupBytes = fileInfo.Length,
                CreatedAtUtc = DateTime.UtcNow,
                Trigger = trigger,
                SchemaVersion = _bootstrapOptions.TargetSchemaVersion,
                Compressed = compressed,
                ChecksumEnabled = _options.UseChecksum,
                Notes = "Albert Retail Terminal SQL Express full backup"
            };

            await File.WriteAllTextAsync(
                    manifestPath,
                    JsonSerializer.Serialize(manifest, JsonOptions),
                    Encoding.UTF8,
                    cancellationToken)
                .ConfigureAwait(false);

            if (_options.VerifyAfterBackup && !VerifyChecksum(bakPath, checksum))
            {
                throw new InvalidOperationException("Post-backup SHA-256 integrity verification failed.");
            }

            await InsertHistoryAsync(manifest, success: true, error: null, cancellationToken).ConfigureAwait(false);
            await PruneOldBackupsAsync(directory, cancellationToken).ConfigureAwait(false);

            if (trigger == DatabaseBackupTriggers.Midnight)
            {
                await UpsertConfigAsync(
                        LastMidnightBackupConfigKey,
                        DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (trigger == DatabaseBackupTriggers.EndOfShift)
            {
                await UpsertConfigAsync(LastShiftBackupConfigKey, DateTime.UtcNow.ToString("O"), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (trigger == DatabaseBackupTriggers.EndOfDay)
            {
                await UpsertConfigAsync(
                        LastEndOfDayBackupConfigKey,
                        DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _lastBackupTime = manifest.CreatedAtUtc;
            _backupFilePath = bakPath;
            _lastError = null;
            _logger.LogInformation("Database backup completed: {Path} ({Bytes} bytes).", bakPath, fileInfo.Length);

            return DatabaseBackupResult.Ok(
                manifest,
                _options.VerifyAfterBackup
                    ? $"Backup saved and integrity-verified: {bakPath}"
                    : $"Backup saved to {bakPath}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastError = ex.Message;
            _logger.LogWarning(ex, "Database backup failed.");
            try
            {
                await InsertHistoryAsync(
                        new DatabaseBackupManifest
                        {
                            BackupFilePath = bakPath,
                            Sha256Checksum = string.Empty,
                            CreatedAtUtc = DateTime.UtcNow,
                            Trigger = trigger,
                            SchemaVersion = _bootstrapOptions.TargetSchemaVersion
                        },
                        success: false,
                        error: ex.Message,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                // History insert is best-effort during failure.
            }

            return DatabaseBackupResult.Failed(ex.Message);
        }
        finally
        {
            _isBackingUp = false;
            RaiseStatusChanged();
            _gate.Release();
        }
    }

    private async Task<bool> ExecuteBackupAsync(string databaseName, string bakPath, CancellationToken cancellationToken)
    {
        // Connect to master so BACKUP can run even if the user DB is busy.
        var masterBuilder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(masterBuilder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        var checksumClause = _options.UseChecksum ? ", CHECKSUM" : string.Empty;
        if (_options.UseCompression)
        {
            try
            {
                await RunBackupCommandAsync(
                        connection,
                        databaseName,
                        bakPath,
                        withCompression: true,
                        checksumClause,
                        cancellationToken)
                    .ConfigureAwait(false);
                return true;
            }
            catch (SqlException ex)
            {
                _logger.LogInformation(
                    ex,
                    "BACKUP WITH COMPRESSION unavailable; retrying without compression.");
            }
        }

        await RunBackupCommandAsync(
                connection,
                databaseName,
                bakPath,
                withCompression: false,
                checksumClause,
                cancellationToken)
            .ConfigureAwait(false);
        return false;
    }

    private async Task RunBackupCommandAsync(
        SqlConnection connection,
        string databaseName,
        string bakPath,
        bool withCompression,
        string checksumClause,
        CancellationToken cancellationToken)
    {
        var compressionClause = withCompression ? ", COMPRESSION" : string.Empty;
        var sql = $"""
            BACKUP DATABASE [{EscapeIdent(databaseName)}]
            TO DISK = @BakPath
            WITH INIT, COPY_ONLY, STATS = 10{compressionClause}{checksumClause};
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = Math.Clamp(_options.CommandTimeoutSeconds, 60, 3600);
        command.Parameters.Add(new SqlParameter("@BakPath", bakPath));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WriteEncryptedSecretsSidecarAsync(string secretsPath, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var configRepo = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        var bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _options.SensitiveConfigKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var json = await configRepo.GetJsonAsync(key, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
            {
                bag[key] = protector.Protect(json);
            }
        }

        // Also protect Terminals.SecretKey values when present.
        try
        {
            var connections = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();
            await using var connection = await connections.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var secrets = await connection.QueryAsync<(string TerminalId, string? SecretKey)>(
                new CommandDefinition(
                    "SELECT TerminalId, SecretKey FROM dbo.Terminals WHERE SecretKey IS NOT NULL;",
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            foreach (var row in secrets)
            {
                if (!string.IsNullOrWhiteSpace(row.SecretKey))
                {
                    bag[$"Terminals.{row.TerminalId}.SecretKey"] = protector.Protect(row.SecretKey);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to export terminal secrets sidecar.");
        }

        if (bag.Count == 0)
        {
            return false;
        }

        var envelope = new
        {
            algorithm = "DPAPI-CurrentUser",
            capturedAtUtc = DateTime.UtcNow,
            secrets = bag
        };

        await File.WriteAllTextAsync(
                secretsPath,
                JsonSerializer.Serialize(envelope, JsonOptions),
                Encoding.UTF8,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    private async Task InsertHistoryAsync(
        DatabaseBackupManifest manifest,
        bool success,
        string? error,
        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();

        const string sql = """
            INSERT INTO dbo.DatabaseBackupHistory
                (CreatedAtUtc, Trigger, BackupFilePath, Sha256Checksum, BackupBytes, Success, ErrorMessage)
            VALUES
                (@CreatedAtUtc, @Trigger, @BackupFilePath, @Sha256Checksum, @BackupBytes, @Success, @ErrorMessage);
            """;

        try
        {
            await using var connection = await connections.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new
                    {
                        CreatedAtUtc = manifest.CreatedAtUtc,
                        Trigger = manifest.Trigger,
                        BackupFilePath = manifest.BackupFilePath,
                        Sha256Checksum = manifest.Sha256Checksum,
                        BackupBytes = manifest.BackupBytes,
                        Success = success,
                        ErrorMessage = Truncate(error, 2000)
                    },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 208 or 207)
        {
            _logger.LogDebug(ex, "DatabaseBackupHistory table missing; filesystem manifest remains authoritative.");
        }
    }

    private async Task PruneOldBackupsAsync(string directory, CancellationToken cancellationToken)
    {
        var retention = Math.Max(1, _options.RetentionCount);
        var bakFiles = Directory.GetFiles(directory, "PointOfSale_*.bak")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();

        var ageCutoff = _options.RetentionDays > 0
            ? DateTime.UtcNow.AddDays(-_options.RetentionDays)
            : (DateTime?)null;

        foreach (var obsolete in bakFiles.Skip(retention))
        {
            TryDelete(obsolete.FullName);
            TryDelete(IOPath.ChangeExtension(obsolete.FullName, ".manifest.json"));
            TryDelete(IOPath.ChangeExtension(obsolete.FullName, ".secrets.dpapi.json"));
        }

        if (ageCutoff is not null)
        {
            foreach (var aged in bakFiles.Where(f => f.CreationTimeUtc < ageCutoff.Value))
            {
                TryDelete(aged.FullName);
                TryDelete(IOPath.ChangeExtension(aged.FullName, ".manifest.json"));
                TryDelete(IOPath.ChangeExtension(aged.FullName, ".secrets.dpapi.json"));
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();
        const string sql = """
            DELETE FROM dbo.DatabaseBackupHistory
            WHERE BackupId NOT IN (
                SELECT TOP (@Retention) BackupId
                FROM dbo.DatabaseBackupHistory
                WHERE Success = 1
                ORDER BY CreatedAtUtc DESC, BackupId DESC
            )
            AND Success = 1;
            """;
        try
        {
            await using var connection = await connections.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                new CommandDefinition(sql, new { Retention = retention }, cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            if (_options.RetentionDays > 0)
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        DELETE FROM dbo.DatabaseBackupHistory
                        WHERE Success = 1
                          AND CreatedAtUtc < DATEADD(DAY, -@Days, SYSUTCDATETIME());
                        """,
                        new { Days = _options.RetentionDays },
                        cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }
        }
        catch (SqlException)
        {
            // Optional cleanup.
        }
    }

    private IReadOnlyList<DatabaseBackupHistoryEntry> LoadHistoryFromFilesystem(int take)
    {
        var directory = ResolveBackupDirectory();
        if (!Directory.Exists(directory))
        {
            return Array.Empty<DatabaseBackupHistoryEntry>();
        }

        return Directory.GetFiles(directory, "*.manifest.json")
            .Select(path =>
            {
                try
                {
                    var manifest = JsonSerializer.Deserialize<DatabaseBackupManifest>(
                        File.ReadAllText(path),
                        JsonOptions);
                    if (manifest is null)
                    {
                        return null;
                    }

                    return new DatabaseBackupHistoryEntry
                    {
                        CreatedAtUtc = manifest.CreatedAtUtc,
                        Trigger = manifest.Trigger,
                        BackupFilePath = manifest.BackupFilePath,
                        Sha256Checksum = manifest.Sha256Checksum,
                        BackupBytes = manifest.BackupBytes,
                        Success = true
                    };
                }
                catch
                {
                    return null;
                }
            })
            .Where(x => x is not null)
            .Cast<DatabaseBackupHistoryEntry>()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(take)
            .ToList();
    }

    private async Task UpsertConfigAsync(string key, string value, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        await config.UpsertJsonAsync(key, value, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static bool VerifyChecksum(string filePath, string expectedSha256Hex)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256Hex) || !File.Exists(filePath))
        {
            return false;
        }

        using var stream = File.OpenRead(filePath);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        return hash.Equals(expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort prune.
        }
    }

    private static string EscapeIdent(string value) => value.Replace("]", "]]", StringComparison.Ordinal);

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];

    private void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class DatabaseBackupBackgroundService : BackgroundService
{
    private readonly IDatabaseBackupService _backupService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<DatabaseBackupOptions> _options;
    private readonly ILogger<DatabaseBackupBackgroundService> _logger;

    public DatabaseBackupBackgroundService(
        IDatabaseBackupService backupService,
        IServiceScopeFactory scopeFactory,
        IOptions<DatabaseBackupOptions> options,
        ILogger<DatabaseBackupBackgroundService> logger)
    {
        _backupService = backupService;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromMinutes(Math.Clamp(_options.Value.SchedulerPollMinutes, 1, 60));
        await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Value.Enabled && _options.Value.BackupAtMidnight)
                {
                    await TryMidnightBackupAsync(stoppingToken).ConfigureAwait(false);
                }

                if (_options.Value.Enabled && _options.Value.BackupOnEndOfDay)
                {
                    await TryEndOfDayBackupAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Scheduled database backup poll failed.");
            }

            try
            {
                await Task.Delay(poll, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TryMidnightBackupAsync(CancellationToken cancellationToken)
    {
        // Window: local 00:00–00:30 once per calendar day.
        var now = DateTime.Now;
        if (now.TimeOfDay > TimeSpan.FromMinutes(30))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        var last = await config.GetJsonAsync(DatabaseBackupService.LastMidnightBackupConfigKey, cancellationToken)
            .ConfigureAwait(false);
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (string.Equals(last, today, StringComparison.Ordinal))
        {
            return;
        }

        _logger.LogInformation("Starting midnight SQL Express backup.");
        await _backupService.BackupNowAsync(DatabaseBackupTriggers.Midnight, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task TryEndOfDayBackupAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;
        var hour = Math.Clamp(options.EndOfDayHourLocal, 0, 23);
        var window = TimeSpan.FromMinutes(Math.Clamp(options.EndOfDayWindowMinutes, 5, 180));
        var now = DateTime.Now;
        var windowStart = DateTime.Today.AddHours(hour);
        if (now < windowStart || now > windowStart.Add(window))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        var last = await config.GetJsonAsync(DatabaseBackupService.LastEndOfDayBackupConfigKey, cancellationToken)
            .ConfigureAwait(false);
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (string.Equals(last, today, StringComparison.Ordinal))
        {
            return;
        }

        _logger.LogInformation("Starting end-of-day SQL Express backup (scheduled window).");
        await _backupService.BackupOnEndOfDayAsync(cancellationToken).ConfigureAwait(false);
    }
}
