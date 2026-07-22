using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Compliance;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Services;

public sealed class ComplianceAuditLoggingService : IComplianceAuditLogger
{
    private const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ILogger<ComplianceAuditLoggingService> _logger;
    private readonly SemaphoreSlim _appendLock = new(1, 1);

    public ComplianceAuditLoggingService(
        ISqlConnectionFactory connectionFactory,
        ILogger<ComplianceAuditLoggingService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task LogEventAsync(
        string category,
        string action,
        string detail,
        bool success = true,
        string? correlationId = null,
        string? operatorUsername = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        await _appendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!await TableExistsAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("ComplianceAuditLog table missing; skipping event {Action}.", action);
                return;
            }

            var previousHash = await connection.ExecuteScalarAsync<string?>(
                new CommandDefinition(
                    """
                    SELECT TOP (1) EntryHash
                    FROM dbo.ComplianceAuditLog
                    ORDER BY EntryId DESC;
                    """,
                    cancellationToken: cancellationToken)).ConfigureAwait(false) ?? GenesisHash;

            var createdAtUtc = DateTime.UtcNow;
            var username = string.IsNullOrWhiteSpace(operatorUsername) ? "system" : operatorUsername.Trim();
            var safeDetail = Truncate(detail, 2000);
            var entryHash = ComputeHash(previousHash, createdAtUtc, category, action, username, safeDetail, success, correlationId);

            const string sql = """
                INSERT INTO dbo.ComplianceAuditLog
                    (CreatedAtUtc, Category, Action, OperatorUsername, CorrelationId, Detail, Success, PreviousHash, EntryHash)
                VALUES
                    (@CreatedAtUtc, @Category, @Action, @OperatorUsername, @CorrelationId, @Detail, @Success, @PreviousHash, @EntryHash);
                """;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new
                    {
                        CreatedAtUtc = createdAtUtc,
                        Category = Truncate(category, 40),
                        Action = Truncate(action, 80),
                        OperatorUsername = Truncate(username, 80),
                        CorrelationId = Truncate(correlationId, 100),
                        Detail = safeDetail,
                        Success = success,
                        PreviousHash = previousHash,
                        EntryHash = entryHash
                    },
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append compliance audit event {Action}.", action);
        }
        finally
        {
            _appendLock.Release();
        }
    }

    public async Task<ComplianceTamperCheckResult> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await TableExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return new ComplianceTamperCheckResult
            {
                IsValid = true,
                EntriesVerified = 0,
                Message = "Compliance audit table not provisioned."
            };
        }

        var rows = (await connection.QueryAsync<ComplianceAuditLogEntry>(
            new CommandDefinition(
                """
                SELECT EntryId, CreatedAtUtc, Category, Action, OperatorUsername, CorrelationId, Detail, Success, PreviousHash, EntryHash
                FROM dbo.ComplianceAuditLog
                ORDER BY EntryId ASC;
                """,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).ToList();

        var expectedPrevious = GenesisHash;
        foreach (var row in rows)
        {
            if (!string.Equals(row.PreviousHash, expectedPrevious, StringComparison.OrdinalIgnoreCase))
            {
                return new ComplianceTamperCheckResult
                {
                    IsValid = false,
                    EntriesVerified = row.EntryId - 1,
                    FirstBrokenEntryId = row.EntryId,
                    Message = $"Hash chain broken at entry {row.EntryId} (previous hash mismatch)."
                };
            }

            var recomputed = ComputeHash(
                row.PreviousHash,
                row.CreatedAtUtc,
                row.Category,
                row.Action,
                row.OperatorUsername,
                row.Detail,
                row.Success,
                row.CorrelationId);

            if (!string.Equals(recomputed, row.EntryHash, StringComparison.OrdinalIgnoreCase))
            {
                return new ComplianceTamperCheckResult
                {
                    IsValid = false,
                    EntriesVerified = row.EntryId - 1,
                    FirstBrokenEntryId = row.EntryId,
                    Message = $"Entry hash mismatch at entry {row.EntryId}."
                };
            }

            expectedPrevious = row.EntryHash;
        }

        return new ComplianceTamperCheckResult
        {
            IsValid = true,
            EntriesVerified = rows.Count,
            Message = rows.Count == 0 ? "No compliance audit entries yet." : "Tamper check passed."
        };
    }

    public async Task<IReadOnlyList<ComplianceAuditLogEntry>> GetRecentAsync(
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await TableExistsAsync(connection, cancellationToken).ConfigureAwait(false))
        {
            return Array.Empty<ComplianceAuditLogEntry>();
        }

        var rows = await connection.QueryAsync<ComplianceAuditLogEntry>(
            new CommandDefinition(
                """
                SELECT TOP (@Take)
                    EntryId, CreatedAtUtc, Category, Action, OperatorUsername, CorrelationId, Detail, Success, PreviousHash, EntryHash
                FROM dbo.ComplianceAuditLog
                ORDER BY EntryId DESC;
                """,
                new { Take = Math.Clamp(take, 1, 500) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.ToList();
    }

    private static string ComputeHash(
        string previousHash,
        DateTime createdAtUtc,
        string category,
        string action,
        string operatorUsername,
        string detail,
        bool success,
        string? correlationId)
    {
        var payload = string.Join(
            "|",
            previousHash,
            createdAtUtc.ToString("O"),
            category,
            action,
            operatorUsername,
            detail,
            success ? "1" : "0",
            correlationId ?? string.Empty);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task<bool> TableExistsAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM sys.tables WHERE name = N'ComplianceAuditLog';",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return count > 0;
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
