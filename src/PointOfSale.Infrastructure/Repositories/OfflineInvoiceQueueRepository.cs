using Dapper;
using Microsoft.Data.SqlClient;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface IOfflineInvoiceQueueRepository
{
    Task<int> EnqueuePendingAsync(string payloadJson, CancellationToken cancellationToken = default);

    Task<int> EnqueuePendingAsync(string payloadJson, SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken = default);

    Task<OfflineInvoiceQueueItem?> GetNextFifoEligibleAsync(CancellationToken cancellationToken = default);

    Task<bool> TryMarkSyncingAsync(int id, CancellationToken cancellationToken = default);

    Task MarkSyncedAsync(int id, string? fiscalResponseJson = null, CancellationToken cancellationToken = default);

    Task<OfflineInvoiceQueueItem?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OfflineInvoiceQueueItem>> GetItemsAsync(
        string? statusFilter,
        int take,
        CancellationToken cancellationToken = default);

    Task MarkQuarantinedAsync(int id, string errorMessage, CancellationToken cancellationToken = default);

    Task MarkPendingRetryAsync(int id, int retryCount, DateTime nextRetryTimeUtc, string errorMessage, CancellationToken cancellationToken = default);

    Task ResetSyncingToPendingAsync(int id, int retryCount, DateTime nextRetryTimeUtc, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>Persists a corrected/normalized payload before Force Sync / Retry resubmit.</summary>
    Task UpdatePayloadJsonAsync(int id, string payloadJson, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, int>> GetStatusCountsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OfflineInvoiceQueueItem>> GetRecentItemsAsync(int take, CancellationToken cancellationToken = default);

    Task<bool> RetryQuarantinedAsync(int id, CancellationToken cancellationToken = default);
}

public sealed class OfflineInvoiceQueueRepository : IOfflineInvoiceQueueRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public OfflineInvoiceQueueRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<int> EnqueuePendingAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        await using var connection = (SqlConnection)await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await EnqueuePendingAsync(payloadJson, connection, transaction: null!, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> EnqueuePendingAsync(
        string payloadJson,
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.OfflineInvoiceQueue (PayloadJson, Status, RetryCount, NextRetryTime)
            OUTPUT INSERTED.Id
            VALUES (@PayloadJson, @Status, 0, NULL);
            """;

        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { PayloadJson = payloadJson, Status = OfflineQueueStatuses.Pending },
                transaction: transaction,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<OfflineInvoiceQueueItem?> GetNextFifoEligibleAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (1)
                Id, PayloadJson, CreatedAt, Status, RetryCount, NextRetryTime, ErrorMessage, FiscalResponseJson
            FROM dbo.OfflineInvoiceQueue AS q WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE q.Status = @PendingStatus
              AND (q.NextRetryTime IS NULL OR q.NextRetryTime <= GETUTCDATE())
              AND NOT EXISTS (
                  SELECT 1
                  FROM dbo.OfflineInvoiceQueue AS blocker
                  WHERE blocker.Status IN (@PendingStatus, @SyncingStatus)
                    AND (
                        blocker.CreatedAt < q.CreatedAt
                        OR (blocker.CreatedAt = q.CreatedAt AND blocker.Id < q.Id)
                    )
              )
            ORDER BY q.CreatedAt ASC, q.Id ASC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<OfflineInvoiceQueueItem>(
            new CommandDefinition(
                sql,
                new
                {
                    PendingStatus = OfflineQueueStatuses.Pending,
                    SyncingStatus = OfflineQueueStatuses.Syncing
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<bool> TryMarkSyncingAsync(int id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.OfflineInvoiceQueue
            SET Status = @SyncingStatus
            WHERE Id = @Id AND Status = @PendingStatus;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    Id = id,
                    PendingStatus = OfflineQueueStatuses.Pending,
                    SyncingStatus = OfflineQueueStatuses.Syncing
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows == 1;
    }

    public async Task MarkSyncedAsync(int id, string? fiscalResponseJson = null, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.OfflineInvoiceQueue
            SET Status = @Status,
                ErrorMessage = NULL,
                NextRetryTime = NULL,
                FiscalResponseJson = @FiscalResponseJson
            WHERE Id = @Id;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Id = id, Status = OfflineQueueStatuses.Synced, FiscalResponseJson = fiscalResponseJson },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<OfflineInvoiceQueueItem?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Id, PayloadJson, CreatedAt, Status, RetryCount, NextRetryTime, ErrorMessage, FiscalResponseJson
            FROM dbo.OfflineInvoiceQueue
            WHERE Id = @Id;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<OfflineInvoiceQueueItem>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OfflineInvoiceQueueItem>> GetItemsAsync(
        string? statusFilter,
        int take,
        CancellationToken cancellationToken = default)
    {
        var sql = string.IsNullOrWhiteSpace(statusFilter)
            ? """
              SELECT TOP (@Take)
                  Id, PayloadJson, CreatedAt, Status, RetryCount, NextRetryTime, ErrorMessage, FiscalResponseJson
              FROM dbo.OfflineInvoiceQueue
              ORDER BY Id DESC;
              """
            : """
              SELECT TOP (@Take)
                  Id, PayloadJson, CreatedAt, Status, RetryCount, NextRetryTime, ErrorMessage, FiscalResponseJson
              FROM dbo.OfflineInvoiceQueue
              WHERE Status = @StatusFilter
              ORDER BY Id DESC;
              """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<OfflineInvoiceQueueItem>(
            new CommandDefinition(
                sql,
                new { Take = take, StatusFilter = statusFilter },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task MarkQuarantinedAsync(int id, string errorMessage, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.OfflineInvoiceQueue
            SET Status = @Status,
                ErrorMessage = @ErrorMessage,
                NextRetryTime = NULL
            WHERE Id = @Id;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Id = id, Status = OfflineQueueStatuses.Quarantined, ErrorMessage = errorMessage },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task MarkPendingRetryAsync(
        int id,
        int retryCount,
        DateTime nextRetryTimeUtc,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.OfflineInvoiceQueue
            SET Status = @Status,
                RetryCount = @RetryCount,
                NextRetryTime = @NextRetryTime,
                ErrorMessage = @ErrorMessage
            WHERE Id = @Id;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    Id = id,
                    Status = OfflineQueueStatuses.Pending,
                    RetryCount = retryCount,
                    NextRetryTime = nextRetryTimeUtc,
                    ErrorMessage = errorMessage
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task ResetSyncingToPendingAsync(
        int id,
        int retryCount,
        DateTime nextRetryTimeUtc,
        string errorMessage,
        CancellationToken cancellationToken = default) =>
        await MarkPendingRetryAsync(id, retryCount, nextRetryTimeUtc, errorMessage, cancellationToken).ConfigureAwait(false);

    public async Task UpdatePayloadJsonAsync(int id, string payloadJson, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.OfflineInvoiceQueue
            SET PayloadJson = @PayloadJson
            WHERE Id = @Id;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Id = id, PayloadJson = payloadJson },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetStatusCountsAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Status, COUNT(*) AS Count
            FROM dbo.OfflineInvoiceQueue
            GROUP BY Status;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Status, int Count)>(
            new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.ToDictionary(x => x.Status, x => x.Count, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<OfflineInvoiceQueueItem>> GetRecentItemsAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                Id, PayloadJson, CreatedAt, Status, RetryCount, NextRetryTime, ErrorMessage, FiscalResponseJson
            FROM dbo.OfflineInvoiceQueue
            ORDER BY Id DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<OfflineInvoiceQueueItem>(
            new CommandDefinition(sql, new { Take = take }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<bool> RetryQuarantinedAsync(int id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.OfflineInvoiceQueue
            SET Status = @PendingStatus,
                RetryCount = 0,
                NextRetryTime = NULL,
                ErrorMessage = NULL
            WHERE Id = @Id AND Status = @QuarantinedStatus;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    Id = id,
                    PendingStatus = OfflineQueueStatuses.Pending,
                    QuarantinedStatus = OfflineQueueStatuses.Quarantined
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows == 1;
    }
}
