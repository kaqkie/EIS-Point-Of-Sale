using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface IGoodsReceiptRepository
{
    Task<long> CreateAsync(GoodsReceiptNote grn, IReadOnlyList<GoodsReceiptLine> lines, CancellationToken cancellationToken = default);
    Task UpdateDraftAsync(GoodsReceiptNote grn, IReadOnlyList<GoodsReceiptLine> lines, CancellationToken cancellationToken = default);
    Task MarkPostedAsync(long grnId, CancellationToken cancellationToken = default);
    Task<GoodsReceiptNote?> GetByIdAsync(long grnId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GoodsReceiptLine>> GetLinesAsync(long grnId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GoodsReceiptNote>> GetRecentAsync(int take = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GoodsReceiptNote>> GetByPoIdAsync(long poId, CancellationToken cancellationToken = default);
}

public interface ISupplierInvoiceReconciliationRepository
{
    Task<long> CreateAsync(
        SupplierInvoiceReconciliation header,
        IReadOnlyList<SupplierInvoiceReconciliationLine> lines,
        CancellationToken cancellationToken = default);
    Task SignOffAsync(long reconciliationId, string? notes, CancellationToken cancellationToken = default);
    Task<SupplierInvoiceReconciliation?> GetByIdAsync(long reconciliationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupplierInvoiceReconciliationLine>> GetLinesAsync(long reconciliationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupplierInvoiceReconciliation>> GetRecentAsync(int take = 50, CancellationToken cancellationToken = default);
}

public sealed class GoodsReceiptRepository : IGoodsReceiptRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public GoodsReceiptRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<long> CreateAsync(
        GoodsReceiptNote grn,
        IReadOnlyList<GoodsReceiptLine> lines,
        CancellationToken cancellationToken = default)
    {
        const string insertHeader = """
            INSERT INTO dbo.GoodsReceiptNotes
                (GrnNumber, PoId, PoNumber, SupplierCode, SupplierName, Status, DeliveryNoteNumber,
                 SupplierInvoiceNumber, OperatorUsername, Notes)
            OUTPUT INSERTED.GrnId
            VALUES
                (@GrnNumber, @PoId, @PoNumber, @SupplierCode, @SupplierName, @Status, @DeliveryNoteNumber,
                 @SupplierInvoiceNumber, @OperatorUsername, @Notes);
            """;

        const string insertLine = """
            INSERT INTO dbo.GoodsReceiptLines
                (GrnId, ProductCode, ProductName, OrderedQty, ReceivedQty, DamagedQty, UnitCost,
                 PreviousStock, NewStock, PreviousAvgCost, NewAvgCost, PreviousRetailPrice, NewRetailPrice, LineNotes)
            VALUES
                (@GrnId, @ProductCode, @ProductName, @OrderedQty, @ReceivedQty, @DamagedQty, @UnitCost,
                 @PreviousStock, @NewStock, @PreviousAvgCost, @NewAvgCost, @PreviousRetailPrice, @NewRetailPrice, @LineNotes);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var grnId = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(insertHeader, grn, tx, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        foreach (var line in lines)
        {
            line.GrnId = grnId;
            await connection.ExecuteAsync(
                    new CommandDefinition(insertLine, line, tx, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return grnId;
    }

    public async Task UpdateDraftAsync(
        GoodsReceiptNote grn,
        IReadOnlyList<GoodsReceiptLine> lines,
        CancellationToken cancellationToken = default)
    {
        const string updateHeader = """
            UPDATE dbo.GoodsReceiptNotes
            SET DeliveryNoteNumber = @DeliveryNoteNumber,
                SupplierInvoiceNumber = @SupplierInvoiceNumber,
                Notes = @Notes,
                OperatorUsername = @OperatorUsername
            WHERE GrnId = @GrnId AND Status = 'Draft';
            """;

        const string deleteLines = "DELETE FROM dbo.GoodsReceiptLines WHERE GrnId = @GrnId;";
        const string insertLine = """
            INSERT INTO dbo.GoodsReceiptLines
                (GrnId, ProductCode, ProductName, OrderedQty, ReceivedQty, DamagedQty, UnitCost,
                 PreviousStock, NewStock, PreviousAvgCost, NewAvgCost, PreviousRetailPrice, NewRetailPrice, LineNotes)
            VALUES
                (@GrnId, @ProductCode, @ProductName, @OrderedQty, @ReceivedQty, @DamagedQty, @UnitCost,
                 @PreviousStock, @NewStock, @PreviousAvgCost, @NewAvgCost, @PreviousRetailPrice, @NewRetailPrice, @LineNotes);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var updated = await connection.ExecuteAsync(
                new CommandDefinition(updateHeader, grn, tx, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (updated == 0)
        {
            throw new InvalidOperationException($"GRN {grn.GrnId} is not a draft or was not found.");
        }

        await connection.ExecuteAsync(
                new CommandDefinition(deleteLines, new { grn.GrnId }, tx, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        foreach (var line in lines)
        {
            line.GrnId = grn.GrnId;
            await connection.ExecuteAsync(
                    new CommandDefinition(insertLine, line, tx, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkPostedAsync(long grnId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.GoodsReceiptNotes
            SET Status = 'Posted', PostedAtUtc = SYSUTCDATETIME()
            WHERE GrnId = @GrnId AND Status = 'Draft';
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(
                new CommandDefinition(sql, new { GrnId = grnId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (rows == 0)
        {
            throw new InvalidOperationException($"Unable to post GRN {grnId} (already posted or missing).");
        }
    }

    public async Task<GoodsReceiptNote?> GetByIdAsync(long grnId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT GrnId, GrnNumber, PoId, PoNumber, SupplierCode, SupplierName, Status, DeliveryNoteNumber,
                   SupplierInvoiceNumber, OperatorUsername, Notes, CreatedAtUtc, PostedAtUtc
            FROM dbo.GoodsReceiptNotes
            WHERE GrnId = @GrnId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<GoodsReceiptNote>(
                new CommandDefinition(sql, new { GrnId = grnId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GoodsReceiptLine>> GetLinesAsync(
        long grnId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT GrnLineId, GrnId, ProductCode, ProductName, OrderedQty, ReceivedQty, DamagedQty, UnitCost,
                   PreviousStock, NewStock, PreviousAvgCost, NewAvgCost, PreviousRetailPrice, NewRetailPrice, LineNotes
            FROM dbo.GoodsReceiptLines
            WHERE GrnId = @GrnId
            ORDER BY GrnLineId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<GoodsReceiptLine>(
                new CommandDefinition(sql, new { GrnId = grnId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<GoodsReceiptNote>> GetRecentAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                GrnId, GrnNumber, PoId, PoNumber, SupplierCode, SupplierName, Status, DeliveryNoteNumber,
                SupplierInvoiceNumber, OperatorUsername, Notes, CreatedAtUtc, PostedAtUtc
            FROM dbo.GoodsReceiptNotes
            ORDER BY CreatedAtUtc DESC, GrnId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<GoodsReceiptNote>(
                new CommandDefinition(sql, new { Take = Math.Clamp(take, 1, 200) }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<GoodsReceiptNote>> GetByPoIdAsync(
        long poId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT GrnId, GrnNumber, PoId, PoNumber, SupplierCode, SupplierName, Status, DeliveryNoteNumber,
                   SupplierInvoiceNumber, OperatorUsername, Notes, CreatedAtUtc, PostedAtUtc
            FROM dbo.GoodsReceiptNotes
            WHERE PoId = @PoId
            ORDER BY CreatedAtUtc DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<GoodsReceiptNote>(
                new CommandDefinition(sql, new { PoId = poId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}

public sealed class SupplierInvoiceReconciliationRepository : ISupplierInvoiceReconciliationRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public SupplierInvoiceReconciliationRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<long> CreateAsync(
        SupplierInvoiceReconciliation header,
        IReadOnlyList<SupplierInvoiceReconciliationLine> lines,
        CancellationToken cancellationToken = default)
    {
        const string insertHeader = """
            INSERT INTO dbo.SupplierInvoiceReconciliations
                (GrnId, GrnNumber, SupplierInvoiceNumber, InvoiceDate, InvoiceTotalMwk, ReceivedTotalMwk,
                 VarianceMwk, Status, DiscrepancyNotes, OperatorUsername)
            OUTPUT INSERTED.ReconciliationId
            VALUES
                (@GrnId, @GrnNumber, @SupplierInvoiceNumber, @InvoiceDate, @InvoiceTotalMwk, @ReceivedTotalMwk,
                 @VarianceMwk, @Status, @DiscrepancyNotes, @OperatorUsername);
            """;

        const string insertLine = """
            INSERT INTO dbo.SupplierInvoiceReconciliationLines
                (ReconciliationId, ProductCode, ProductName, DiscrepancyType, OrderedQty, ReceivedQty, DamagedQty,
                 InvoiceQty, UnitCost, InvoiceUnitCost, Message)
            VALUES
                (@ReconciliationId, @ProductCode, @ProductName, @DiscrepancyType, @OrderedQty, @ReceivedQty, @DamagedQty,
                 @InvoiceQty, @UnitCost, @InvoiceUnitCost, @Message);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var id = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(insertHeader, header, tx, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        foreach (var line in lines)
        {
            line.ReconciliationId = id;
            await connection.ExecuteAsync(
                    new CommandDefinition(insertLine, line, tx, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task SignOffAsync(long reconciliationId, string? notes, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.SupplierInvoiceReconciliations
            SET Status = 'SignedOff',
                SignedOffAtUtc = SYSUTCDATETIME(),
                DiscrepancyNotes = COALESCE(@Notes, DiscrepancyNotes)
            WHERE ReconciliationId = @ReconciliationId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new { ReconciliationId = reconciliationId, Notes = notes },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<SupplierInvoiceReconciliation?> GetByIdAsync(
        long reconciliationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ReconciliationId, GrnId, GrnNumber, SupplierInvoiceNumber, InvoiceDate, InvoiceTotalMwk,
                   ReceivedTotalMwk, VarianceMwk, Status, DiscrepancyNotes, OperatorUsername, CreatedAtUtc, SignedOffAtUtc
            FROM dbo.SupplierInvoiceReconciliations
            WHERE ReconciliationId = @ReconciliationId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<SupplierInvoiceReconciliation>(
                new CommandDefinition(sql, new { ReconciliationId = reconciliationId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SupplierInvoiceReconciliationLine>> GetLinesAsync(
        long reconciliationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ReconciliationLineId, ReconciliationId, ProductCode, ProductName, DiscrepancyType,
                   OrderedQty, ReceivedQty, DamagedQty, InvoiceQty, UnitCost, InvoiceUnitCost, Message
            FROM dbo.SupplierInvoiceReconciliationLines
            WHERE ReconciliationId = @ReconciliationId
            ORDER BY ReconciliationLineId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<SupplierInvoiceReconciliationLine>(
                new CommandDefinition(sql, new { ReconciliationId = reconciliationId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<SupplierInvoiceReconciliation>> GetRecentAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                ReconciliationId, GrnId, GrnNumber, SupplierInvoiceNumber, InvoiceDate, InvoiceTotalMwk,
                ReceivedTotalMwk, VarianceMwk, Status, DiscrepancyNotes, OperatorUsername, CreatedAtUtc, SignedOffAtUtc
            FROM dbo.SupplierInvoiceReconciliations
            ORDER BY CreatedAtUtc DESC, ReconciliationId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<SupplierInvoiceReconciliation>(
                new CommandDefinition(sql, new { Take = Math.Clamp(take, 1, 200) }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}
