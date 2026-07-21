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

    Task MarkSyncedAsync(int id, CancellationToken cancellationToken = default);

    Task MarkQuarantinedAsync(int id, string errorMessage, CancellationToken cancellationToken = default);

    Task MarkPendingRetryAsync(int id, int retryCount, DateTime nextRetryTimeUtc, string errorMessage, CancellationToken cancellationToken = default);

    Task ResetSyncingToPendingAsync(int id, int retryCount, DateTime nextRetryTimeUtc, string errorMessage, CancellationToken cancellationToken = default);
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
                q.Id, q.PayloadJson, q.CreatedAt, q.Status, q.RetryCount, q.NextRetryTime, q.ErrorMessage
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

    public async Task MarkSyncedAsync(int id, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.OfflineInvoiceQueue
            SET Status = @Status,
                ErrorMessage = NULL,
                NextRetryTime = NULL
            WHERE Id = @Id;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Id = id, Status = OfflineQueueStatuses.Synced },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
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
}
