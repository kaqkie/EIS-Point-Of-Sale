using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface IArchivalCompressionService
{
    bool IsArchiving { get; }

    Task<ArchivalCompressionResult> ArchiveStaleDataAsync(
        FiscalDualKeyAuthorization authorization,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FiscalArchivePackageRecord>> GetRecentPackagesAsync(
        int take = 30,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Exports sales, void, and telemetry data older than the configured retention window
/// into dual-password encrypted .art-fiscal packages.
/// </summary>
public sealed class ArchivalCompressionService : IArchivalCompressionService
{
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IFiscalYearArchiveRepository _archiveRepository;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly FiscalArchivalOptions _options;
    private readonly ILogger<ArchivalCompressionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _isArchiving;

    public ArchivalCompressionService(
        ISqlConnectionFactory connectionFactory,
        IFiscalYearArchiveRepository archiveRepository,
        IAuthenticationAuthorizationService auth,
        IOptions<FiscalArchivalOptions> options,
        ILogger<ArchivalCompressionService> logger)
    {
        _connectionFactory = connectionFactory;
        _archiveRepository = archiveRepository;
        _auth = auth;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsArchiving => _isArchiving;

    public async Task<ArchivalCompressionResult> ArchiveStaleDataAsync(
        FiscalDualKeyAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.ExecuteFiscalYearRollover);
        var primary = _auth.CurrentOperator
            ?? throw new InvalidOperationException("Sign in before archiving data.");

        if (string.IsNullOrWhiteSpace(authorization.PrimaryArchivePassword)
            || string.IsNullOrWhiteSpace(authorization.SecondaryArchivePassword))
        {
            throw new InvalidOperationException("Dual archive passwords are required.");
        }

        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new ArchivalCompressionResult
            {
                Success = false,
                Message = "Another archival operation is already in progress."
            };
        }

        try
        {
            _isArchiving = true;
            var cutoff = DateTime.UtcNow.AddMonths(-Math.Max(1, _options.StaleDataAgeMonths));

            var sales = await LoadStaleSalesAsync(cutoff, cancellationToken).ConfigureAwait(false);
            var telemetry = await LoadStaleTelemetryAsync(cutoff, cancellationToken).ConfigureAwait(false);
            var voidRows = sales.Where(IsVoidLike).ToList();

            if (sales.Count == 0 && telemetry.Count == 0)
            {
                return new ArchivalCompressionResult
                {
                    Success = true,
                    Message = $"No data older than {_options.StaleDataAgeMonths} months to archive."
                };
            }

            var payload = new
            {
                archivedAtUtc = DateTime.UtcNow,
                cutoffUtc = cutoff,
                archivedBy = primary.Username,
                secondarySupervisor = authorization.SecondarySupervisorUsername.Trim(),
                sales,
                voidLogs = voidRows,
                telemetry
            };

            var payloadJson = JsonSerializer.Serialize(payload);
            var manifestJson = FiscalArchivePasswordCipher.SerializeManifest(new
            {
                packageType = FiscalArchivePackageTypes.StaleDataCompression,
                cutoffUtc = cutoff,
                salesCount = sales.Count,
                telemetryCount = telemetry.Count,
                voidCount = voidRows.Count
            });

            var key = FiscalArchivePasswordCipher.DeriveDualKey(
                authorization.PrimaryArchivePassword,
                authorization.SecondaryArchivePassword);
            var fileBytes = FiscalArchivePasswordCipher.BuildArtFiscalFile(
                Encoding.UTF8.GetBytes(manifestJson),
                Encoding.UTF8.GetBytes(payloadJson),
                key);
            CryptographicOperations.ZeroMemory(key);

            var directory = ResolveArchiveDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"StaleData_{DateTime.UtcNow:yyyyMMddHHmmss}.art-fiscal");
            await File.WriteAllBytesAsync(path, fileBytes, cancellationToken).ConfigureAwait(false);

            await _archiveRepository.InsertPackageAsync(
                    new FiscalArchivePackageRecord
                    {
                        PackageType = FiscalArchivePackageTypes.StaleDataCompression,
                        PeriodStartUtc = cutoff,
                        PeriodEndUtc = DateTime.UtcNow,
                        FilePath = path,
                        FileBytes = fileBytes.Length,
                        ContentSha256 = Convert.ToHexString(SHA256.HashData(fileBytes)),
                        TriggeredByUsername = primary.Username,
                        DualKeyProtected = true
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var purgedSales = 0;
            var purgedTelemetry = 0;
            if (_options.PurgeArchivedSalesFromActiveDatabase && sales.Count > 0)
            {
                purgedSales = await PurgeSalesAsync(cutoff, cancellationToken).ConfigureAwait(false);
            }

            if (_options.PurgeArchivedTelemetryFromActiveDatabase && telemetry.Count > 0)
            {
                purgedTelemetry = await PurgeTelemetryAsync(cutoff, cancellationToken).ConfigureAwait(false);
            }

            PurgeRotatedLogFiles(cutoff);

            _logger.LogInformation(
                "Stale archival package {Path} ({Bytes} bytes). Purged sales={Sales} telemetry={Telemetry}",
                path,
                fileBytes.Length,
                purgedSales,
                purgedTelemetry);

            return new ArchivalCompressionResult
            {
                Success = true,
                Message = $"Archived {sales.Count} sale(s) and {telemetry.Count} telemetry row(s) to {path}.",
                PackagePath = path,
                BytesWritten = fileBytes.Length,
                SalesRowsArchived = sales.Count,
                TelemetryRowsArchived = telemetry.Count
            };
        }
        finally
        {
            _isArchiving = false;
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<FiscalArchivePackageRecord>> GetRecentPackagesAsync(
        int take = 30,
        CancellationToken cancellationToken = default) =>
        _archiveRepository.GetRecentPackagesAsync(take, cancellationToken);

    private async Task<List<StaleSalesRow>> LoadStaleSalesAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                Id,
                CreatedAt,
                Status,
                PayloadJson,
                FiscalResponseJson
            FROM dbo.OfflineInvoiceQueue
            WHERE CreatedAt < @Cutoff
              AND Status IN (N'SYNCED', N'QUARANTINED')
            ORDER BY CreatedAt ASC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<StaleSalesRow>(
                new CommandDefinition(sql, new { Cutoff = cutoffUtc }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    private async Task<List<StaleTelemetryRow>> LoadStaleTelemetryAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT EventId, CreatedAtUtc, Category, Severity, Source, Message, DetailJson
            FROM dbo.DiagnosticTelemetryEvents
            WHERE CreatedAtUtc < @Cutoff
            ORDER BY CreatedAtUtc ASC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<StaleTelemetryRow>(
                new CommandDefinition(sql, new { Cutoff = cutoffUtc }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    private async Task<int> PurgeSalesAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM dbo.OfflineInvoiceQueue
            WHERE CreatedAt < @Cutoff AND Status IN (N'SYNCED', N'QUARANTINED');
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteAsync(
                new CommandDefinition(sql, new { Cutoff = cutoffUtc }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private async Task<int> PurgeTelemetryAsync(DateTime cutoffUtc, CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM dbo.DiagnosticTelemetryEvents WHERE CreatedAtUtc < @Cutoff;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteAsync(
                new CommandDefinition(sql, new { Cutoff = cutoffUtc }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private void PurgeRotatedLogFiles(DateTime cutoffUtc)
    {
        foreach (var relative in new[] { "Logs/Diagnostics", "Logs/MraAudit" })
        {
            var dir = Path.Combine(AppContext.BaseDirectory, relative);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    private static bool IsVoidLike(StaleSalesRow row)
    {
        if (string.IsNullOrWhiteSpace(row.PayloadJson))
        {
            return false;
        }

        return row.PayloadJson.Contains("void", StringComparison.OrdinalIgnoreCase)
               || row.PayloadJson.Contains("credit", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveArchiveDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.ArchiveDirectory))
        {
            return Environment.ExpandEnvironmentVariables(_options.ArchiveDirectory.Trim());
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AlbertRetailTerminal",
            "FiscalArchives");
    }

    private sealed class StaleSalesRow
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? PayloadJson { get; set; }
        public string? FiscalResponseJson { get; set; }
    }

    private sealed class StaleTelemetryRow
    {
        public long EventId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string? DetailJson { get; set; }
    }
}

/// <summary>Periodic scan for stale operational data eligible for compression archival.</summary>
public sealed class ArchivalCompressionBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FiscalArchivalOptions _options;
    private readonly ILogger<ArchivalCompressionBackgroundService> _logger;

    public ArchivalCompressionBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<FiscalArchivalOptions> options,
        ILogger<ArchivalCompressionBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableBackgroundArchiving)
        {
            _logger.LogInformation("ArchivalCompressionBackgroundService is disabled.");
            return;
        }

        var interval = TimeSpan.FromHours(Math.Max(24, _options.BackgroundScanIntervalHours));
        await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogDebug("Background archival scan idle — operator must run secure archive from Fiscal Rollover UI.");
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
