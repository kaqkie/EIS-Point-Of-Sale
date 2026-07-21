using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface IHeadOfficeSyncOutboxRepository
{
    Task<long> EnqueueAsync(
        string payloadType,
        string correlationKey,
        string plainJson,
        CancellationToken cancellationToken = default);

    Task<bool> ExistsPendingOrUploadedAsync(
        string payloadType,
        string correlationKey,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HeadOfficeSyncOutboxItem>> GetPendingAsync(
        int take,
        CancellationToken cancellationToken = default);

    Task MarkUploadingAsync(IReadOnlyList<long> outboxIds, CancellationToken cancellationToken = default);

    Task MarkUploadedAsync(IReadOnlyList<long> outboxIds, CancellationToken cancellationToken = default);

    Task MarkFailedAsync(IReadOnlyList<long> outboxIds, string errorMessage, CancellationToken cancellationToken = default);

    Task<(int Pending, int Failed, int Uploaded)> GetCountsAsync(CancellationToken cancellationToken = default);
}

public sealed class HeadOfficeSyncOutboxRepository : IHeadOfficeSyncOutboxRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public HeadOfficeSyncOutboxRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<long> EnqueueAsync(
        string payloadType,
        string correlationKey,
        string plainJson,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.HeadOfficeSyncOutbox (PayloadType, CorrelationKey, PlainJson, Status)
            OUTPUT INSERTED.OutboxId
            VALUES (@PayloadType, @CorrelationKey, @PlainJson, @Status);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                sql,
                new
                {
                    PayloadType = payloadType,
                    CorrelationKey = correlationKey,
                    PlainJson = plainJson,
                    Status = HeadOfficeSyncOutboxStatuses.Pending
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<bool> ExistsPendingOrUploadedAsync(
        string payloadType,
        string correlationKey,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM dbo.HeadOfficeSyncOutbox
                WHERE PayloadType = @PayloadType
                  AND CorrelationKey = @CorrelationKey
                  AND Status IN (@Pending, @Uploading, @Uploaded)
            ) THEN 1 ELSE 0 END;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new
                {
                    PayloadType = payloadType,
                    CorrelationKey = correlationKey,
                    Pending = HeadOfficeSyncOutboxStatuses.Pending,
                    Uploading = HeadOfficeSyncOutboxStatuses.Uploading,
                    Uploaded = HeadOfficeSyncOutboxStatuses.Uploaded
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false) == 1;
    }

    public async Task<IReadOnlyList<HeadOfficeSyncOutboxItem>> GetPendingAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                OutboxId, PayloadType, CorrelationKey, PlainJson, Status,
                CreatedAtUtc, UploadedAtUtc, ErrorMessage, AttemptCount
            FROM dbo.HeadOfficeSyncOutbox
            WHERE Status IN (@Pending, @Failed)
            ORDER BY CreatedAtUtc ASC, OutboxId ASC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<HeadOfficeSyncOutboxItem>(
            new CommandDefinition(
                sql,
                new
                {
                    Take = take,
                    Pending = HeadOfficeSyncOutboxStatuses.Pending,
                    Failed = HeadOfficeSyncOutboxStatuses.Failed
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task MarkUploadingAsync(IReadOnlyList<long> outboxIds, CancellationToken cancellationToken = default)
    {
        if (outboxIds.Count == 0)
        {
            return;
        }

        const string sql = """
            UPDATE dbo.HeadOfficeSyncOutbox
            SET Status = @Status,
                AttemptCount = AttemptCount + 1,
                ErrorMessage = NULL
            WHERE OutboxId IN @OutboxIds;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Status = HeadOfficeSyncOutboxStatuses.Uploading, OutboxIds = outboxIds },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task MarkUploadedAsync(IReadOnlyList<long> outboxIds, CancellationToken cancellationToken = default)
    {
        if (outboxIds.Count == 0)
        {
            return;
        }

        const string sql = """
            UPDATE dbo.HeadOfficeSyncOutbox
            SET Status = @Status,
                UploadedAtUtc = SYSUTCDATETIME(),
                ErrorMessage = NULL
            WHERE OutboxId IN @OutboxIds;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Status = HeadOfficeSyncOutboxStatuses.Uploaded, OutboxIds = outboxIds },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(
        IReadOnlyList<long> outboxIds,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        if (outboxIds.Count == 0)
        {
            return;
        }

        const string sql = """
            UPDATE dbo.HeadOfficeSyncOutbox
            SET Status = @Status,
                ErrorMessage = @ErrorMessage
            WHERE OutboxId IN @OutboxIds;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    Status = HeadOfficeSyncOutboxStatuses.Failed,
                    ErrorMessage = Truncate(errorMessage, 2000),
                    OutboxIds = outboxIds
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<(int Pending, int Failed, int Uploaded)> GetCountsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Status, COUNT(*) AS Cnt
            FROM dbo.HeadOfficeSyncOutbox
            GROUP BY Status;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<(string Status, int Cnt)>(
            new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var pending = 0;
        var failed = 0;
        var uploaded = 0;
        foreach (var row in rows)
        {
            if (row.Status is HeadOfficeSyncOutboxStatuses.Pending or HeadOfficeSyncOutboxStatuses.Uploading)
            {
                pending += row.Cnt;
            }
            else if (row.Status == HeadOfficeSyncOutboxStatuses.Failed)
            {
                failed += row.Cnt;
            }
            else if (row.Status == HeadOfficeSyncOutboxStatuses.Uploaded)
            {
                uploaded += row.Cnt;
            }
        }

        return (pending, failed, uploaded);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
