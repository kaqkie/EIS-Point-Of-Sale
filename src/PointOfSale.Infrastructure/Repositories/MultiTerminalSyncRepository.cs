using Dapper;
using Microsoft.Data.SqlClient;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface IMultiTerminalSyncRepository
{
    Task UpsertHeartbeatAsync(TerminalHeartbeatRow row, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TerminalHeartbeatRow>> GetHeartbeatsAsync(
        string branchId,
        CancellationToken cancellationToken = default);

    Task MarkStaleOfflineAsync(
        string branchId,
        DateTime staleBeforeUtc,
        CancellationToken cancellationToken = default);

    Task<long> EnqueueLedgerAsync(
        MultiTerminalSyncLedgerItem item,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MultiTerminalSyncLedgerItem>> GetPendingLedgerAsync(
        string branchId,
        string excludeTerminalId,
        long afterSequence,
        int take,
        CancellationToken cancellationToken = default);

    Task MarkLedgerAppliedAsync(
        long ledgerId,
        string appliedByTerminalId,
        CancellationToken cancellationToken = default);

    Task<long?> GetLastAppliedSequenceAsync(
        string branchId,
        string terminalId,
        CancellationToken cancellationToken = default);

    Task SetLastAppliedSequenceAsync(
        string branchId,
        string terminalId,
        long sequenceNumber,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies an inventory quantity delta under an exclusive SQL application lock to prevent race conditions.
    /// </summary>
    Task<bool> ApplyInventoryDeltaWithLockAsync(
        string productCode,
        decimal quantityDelta,
        int lockTimeoutMs,
        CancellationToken cancellationToken = default);

    Task<int> CountPendingOfflineInvoicesAsync(CancellationToken cancellationToken = default);
}

public sealed class MultiTerminalSyncRepository : IMultiTerminalSyncRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public MultiTerminalSyncRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task UpsertHeartbeatAsync(TerminalHeartbeatRow row, CancellationToken cancellationToken = default)
    {
        const string sql = """
            MERGE dbo.TerminalHeartbeat AS target
            USING (SELECT @TerminalId AS TerminalId) AS source
            ON target.TerminalId = source.TerminalId
            WHEN MATCHED THEN
                UPDATE SET
                    BranchId = @BranchId,
                    LastSeenUtc = @LastSeenUtc,
                    Status = @Status,
                    HostName = @HostName,
                    PendingOfflineInvoices = @PendingOfflineInvoices,
                    OpenShiftExpectedCash = @OpenShiftExpectedCash,
                    OpenShiftCashier = @OpenShiftCashier
            WHEN NOT MATCHED THEN
                INSERT (TerminalId, BranchId, LastSeenUtc, Status, HostName,
                        PendingOfflineInvoices, OpenShiftExpectedCash, OpenShiftCashier)
                VALUES (@TerminalId, @BranchId, @LastSeenUtc, @Status, @HostName,
                        @PendingOfflineInvoices, @OpenShiftExpectedCash, @OpenShiftCashier);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, row, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TerminalHeartbeatRow>> GetHeartbeatsAsync(
        string branchId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT HeartbeatId, TerminalId, BranchId, LastSeenUtc, Status, HostName,
                   PendingOfflineInvoices, OpenShiftExpectedCash, OpenShiftCashier
            FROM dbo.TerminalHeartbeat
            WHERE BranchId = @BranchId
            ORDER BY TerminalId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<TerminalHeartbeatRow>(
            new CommandDefinition(sql, new { BranchId = branchId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task MarkStaleOfflineAsync(
        string branchId,
        DateTime staleBeforeUtc,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.TerminalHeartbeat
            SET Status = N'Offline'
            WHERE BranchId = @BranchId
              AND LastSeenUtc < @StaleBeforeUtc
              AND Status <> N'Offline';
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { BranchId = branchId, StaleBeforeUtc = staleBeforeUtc },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<long> EnqueueLedgerAsync(
        MultiTerminalSyncLedgerItem item,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.MultiTerminalSyncLedger
                (BranchId, SourceTerminalId, EventType, EntityKey, PayloadJson, SequenceNumber, CreatedAtUtc)
            OUTPUT INSERTED.LedgerId
            VALUES
                (@BranchId, @SourceTerminalId, @EventType, @EntityKey, @PayloadJson,
                 NEXT VALUE FOR dbo.Seq_MultiTerminalSync, SYSUTCDATETIME());
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(sql, item, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MultiTerminalSyncLedgerItem>> GetPendingLedgerAsync(
        string branchId,
        string excludeTerminalId,
        long afterSequence,
        int take,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                LedgerId, BranchId, SourceTerminalId, EventType, EntityKey, PayloadJson,
                SequenceNumber, CreatedAtUtc, AppliedAtUtc, AppliedByTerminalId
            FROM dbo.MultiTerminalSyncLedger
            WHERE BranchId = @BranchId
              AND SourceTerminalId <> @ExcludeTerminalId
              AND SequenceNumber > @AfterSequence
            ORDER BY SequenceNumber ASC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<MultiTerminalSyncLedgerItem>(
            new CommandDefinition(
                sql,
                new
                {
                    BranchId = branchId,
                    ExcludeTerminalId = excludeTerminalId,
                    AfterSequence = afterSequence,
                    Take = take
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task MarkLedgerAppliedAsync(
        long ledgerId,
        string appliedByTerminalId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.MultiTerminalSyncLedger
            SET AppliedAtUtc = SYSUTCDATETIME(),
                AppliedByTerminalId = @AppliedByTerminalId
            WHERE LedgerId = @LedgerId
              AND AppliedAtUtc IS NULL;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { LedgerId = ledgerId, AppliedByTerminalId = appliedByTerminalId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<long?> GetLastAppliedSequenceAsync(
        string branchId,
        string terminalId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT LastAppliedSequence
            FROM dbo.MultiTerminalSyncCursor
            WHERE BranchId = @BranchId AND TerminalId = @TerminalId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(
                sql,
                new { BranchId = branchId, TerminalId = terminalId },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task SetLastAppliedSequenceAsync(
        string branchId,
        string terminalId,
        long sequenceNumber,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            MERGE dbo.MultiTerminalSyncCursor AS target
            USING (SELECT @BranchId AS BranchId, @TerminalId AS TerminalId) AS source
            ON target.BranchId = source.BranchId AND target.TerminalId = source.TerminalId
            WHEN MATCHED THEN
                UPDATE SET LastAppliedSequence = @SequenceNumber, UpdatedAtUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (BranchId, TerminalId, LastAppliedSequence, UpdatedAtUtc)
                VALUES (@BranchId, @TerminalId, @SequenceNumber, SYSUTCDATETIME());
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { BranchId = branchId, TerminalId = terminalId, SequenceNumber = sequenceNumber },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<bool> ApplyInventoryDeltaWithLockAsync(
        string productCode,
        decimal quantityDelta,
        int lockTimeoutMs,
        CancellationToken cancellationToken = default)
    {
        await using var connection = (SqlConnection)await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        try
        {
            var resource = "ART_Inv_" + productCode.Trim().ToUpperInvariant();
            if (resource.Length > 255)
            {
                resource = resource[..255];
            }

            var lockResult = await connection.ExecuteScalarAsync<int>(
                new CommandDefinition(
                    """
                    DECLARE @result INT;
                    EXEC @result = sp_getapplock
                        @Resource = @Resource,
                        @LockMode = N'Exclusive',
                        @LockOwner = N'Transaction',
                        @LockTimeout = @LockTimeout;
                    SELECT @result;
                    """,
                    new { Resource = resource, LockTimeout = Math.Max(0, lockTimeoutMs) },
                    transaction: tx,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            if (lockResult < 0)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            var updated = await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE dbo.LocalInventory
                    SET StockQuantity = StockQuantity + @Delta
                    WHERE ProductCode = @ProductCode;
                    """,
                    new { ProductCode = productCode, Delta = quantityDelta },
                    transaction: tx,
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
            return updated > 0;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<int> CountPendingOfflineInvoicesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COUNT(1)
            FROM dbo.OfflineInvoiceQueue
            WHERE Status IN (N'Pending', N'Syncing');
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
