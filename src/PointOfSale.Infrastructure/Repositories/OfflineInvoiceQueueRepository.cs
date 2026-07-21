using Dapper;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface IOfflineInvoiceQueueRepository
{
    Task<int> EnqueueAsync(string payloadJson, CancellationToken cancellationToken = default);
    Task<OfflineInvoiceQueueItem?> DequeueNextPendingAsync(CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(int id, string status, string? errorMessage, int retryCount, DateTime? nextRetryTime, CancellationToken cancellationToken = default);
}

public sealed class OfflineInvoiceQueueRepository : IOfflineInvoiceQueueRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public OfflineInvoiceQueueRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<int> EnqueueAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.OfflineInvoiceQueue (PayloadJson, Status, RetryCount)
            OUTPUT INSERTED.Id
            VALUES (@PayloadJson, @Status, 0);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { PayloadJson = payloadJson, Status = OfflineQueueStatuses.Pending },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<OfflineInvoiceQueueItem?> DequeueNextPendingAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (1)
                Id, PayloadJson, CreatedAt, Status, RetryCount, NextRetryTime, ErrorMessage
            FROM dbo.OfflineInvoiceQueue WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE Status = @Status
              AND (NextRetryTime IS NULL OR NextRetryTime <= GETUTCDATE())
            ORDER BY CreatedAt, Id;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<OfflineInvoiceQueueItem>(
            new CommandDefinition(sql, new { Status = OfflineQueueStatuses.Pending }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateStatusAsync(
        int id,
        string status,
        string? errorMessage,
        int retryCount,
        DateTime? nextRetryTime,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.OfflineInvoiceQueue
            SET Status = @Status,
                ErrorMessage = @ErrorMessage,
                RetryCount = @RetryCount,
                NextRetryTime = @NextRetryTime
            WHERE Id = @Id;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Id = id, Status = status, ErrorMessage = errorMessage, RetryCount = retryCount, NextRetryTime = nextRetryTime },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
