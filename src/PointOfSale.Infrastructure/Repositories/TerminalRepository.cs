using Dapper;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface ITerminalRepository
{
    Task<Terminal?> GetByIdAsync(string terminalId, CancellationToken cancellationToken = default);
    Task UpsertPendingActivationAsync(Terminal terminal, CancellationToken cancellationToken = default);
    Task MarkActivatedAsync(string terminalId, string protectedSecretKey, CancellationToken cancellationToken = default);
    Task UpdateLastSyncedAsync(string terminalId, DateTime syncedAtUtc, CancellationToken cancellationToken = default);
    Task<string?> GetActiveTerminalIdAsync(CancellationToken cancellationToken = default);
}

public sealed class TerminalRepository : ITerminalRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public TerminalRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<Terminal?> GetByIdAsync(string terminalId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TerminalId, BranchCode, ActivationState, SecretKey, LastSyncedAt
            FROM dbo.Terminals
            WHERE TerminalId = @TerminalId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<Terminal>(
            new CommandDefinition(sql, new { TerminalId = terminalId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpsertPendingActivationAsync(Terminal terminal, CancellationToken cancellationToken = default)
    {
        const string sql = """
            MERGE dbo.Terminals AS target
            USING (SELECT @TerminalId AS TerminalId) AS source
            ON target.TerminalId = source.TerminalId
            WHEN MATCHED THEN
                UPDATE SET
                    BranchCode = @BranchCode,
                    ActivationState = @ActivationState,
                    LastSyncedAt = @LastSyncedAt
            WHEN NOT MATCHED THEN
                INSERT (TerminalId, BranchCode, ActivationState, SecretKey, LastSyncedAt)
                VALUES (@TerminalId, @BranchCode, @ActivationState, NULL, @LastSyncedAt);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(sql, terminal, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task MarkActivatedAsync(string terminalId, string protectedSecretKey, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.Terminals
            SET ActivationState = @ActivationState,
                SecretKey = @SecretKey,
                LastSyncedAt = GETUTCDATE()
            WHERE TerminalId = @TerminalId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    TerminalId = terminalId,
                    ActivationState = TerminalActivationStates.Activated,
                    SecretKey = protectedSecretKey
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateLastSyncedAsync(string terminalId, DateTime syncedAtUtc, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.Terminals SET LastSyncedAt = @LastSyncedAt WHERE TerminalId = @TerminalId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { TerminalId = terminalId, LastSyncedAt = syncedAtUtc }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<string?> GetActiveTerminalIdAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (1) TerminalId
            FROM dbo.Terminals
            WHERE ActivationState = @ActivationState
            ORDER BY LastSyncedAt DESC, TerminalId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<string>(
            new CommandDefinition(
                sql,
                new { ActivationState = TerminalActivationStates.Activated },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
