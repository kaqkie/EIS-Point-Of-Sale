using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface ILabelPrintBatchRepository
{
    Task<long> CreateBatchAsync(LabelPrintBatch batch, IReadOnlyList<LabelPrintBatchLine> lines, CancellationToken cancellationToken = default);
    Task MarkPrintedAsync(long batchId, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(long batchId, string? notes, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LabelPrintBatch>> GetRecentAsync(int take = 30, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LabelPrintBatchLine>> GetLinesAsync(long batchId, CancellationToken cancellationToken = default);
}

public sealed class LabelPrintBatchRepository : ILabelPrintBatchRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public LabelPrintBatchRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<long> CreateBatchAsync(
        LabelPrintBatch batch,
        IReadOnlyList<LabelPrintBatchLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(lines);

        const string insertBatch = """
            INSERT INTO dbo.LabelPrintBatches
                (TemplateType, QuantityPerItem, ProductCount, LabelCount, Status, OperatorUsername, Notes)
            OUTPUT INSERTED.BatchId
            VALUES
                (@TemplateType, @QuantityPerItem, @ProductCount, @LabelCount, @Status, @OperatorUsername, @Notes);
            """;

        const string insertLine = """
            INSERT INTO dbo.LabelPrintBatchLines
                (BatchId, ProductCode, ProductName, UnitPriceNet, UnitPriceGross, Quantity, Symbology)
            VALUES
                (@BatchId, @ProductCode, @ProductName, @UnitPriceNet, @UnitPriceGross, @Quantity, @Symbology);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var batchId = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(insertBatch, batch, tx, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        foreach (var line in lines)
        {
            line.BatchId = batchId;
            await connection.ExecuteAsync(
                    new CommandDefinition(insertLine, line, tx, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return batchId;
    }

    public async Task MarkPrintedAsync(long batchId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.LabelPrintBatches
            SET Status = @Status, PrintedAtUtc = SYSUTCDATETIME()
            WHERE BatchId = @BatchId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new { BatchId = batchId, Status = LabelBatchStatuses.Printed },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(long batchId, string? notes, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.LabelPrintBatches
            SET Status = @Status, Notes = COALESCE(@Notes, Notes)
            WHERE BatchId = @BatchId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new { BatchId = batchId, Status = LabelBatchStatuses.Failed, Notes = notes },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LabelPrintBatch>> GetRecentAsync(
        int take = 30,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                BatchId, TemplateType, QuantityPerItem, ProductCount, LabelCount,
                Status, OperatorUsername, Notes, CreatedAtUtc, PrintedAtUtc
            FROM dbo.LabelPrintBatches
            ORDER BY CreatedAtUtc DESC, BatchId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<LabelPrintBatch>(
                new CommandDefinition(sql, new { Take = Math.Clamp(take, 1, 200) }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<LabelPrintBatchLine>> GetLinesAsync(
        long batchId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT BatchLineId, BatchId, ProductCode, ProductName, UnitPriceNet, UnitPriceGross, Quantity, Symbology
            FROM dbo.LabelPrintBatchLines
            WHERE BatchId = @BatchId
            ORDER BY BatchLineId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<LabelPrintBatchLine>(
                new CommandDefinition(sql, new { BatchId = batchId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}
