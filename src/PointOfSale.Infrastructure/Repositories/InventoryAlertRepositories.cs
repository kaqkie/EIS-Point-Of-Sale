using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface IInventorySupplierRepository
{
    Task<IReadOnlyList<InventorySupplier>> GetAllAsync(bool activeOnly = true, CancellationToken cancellationToken = default);
    Task UpsertAsync(InventorySupplier supplier, CancellationToken cancellationToken = default);
}

public interface IInventoryStockAlertRepository
{
    Task UpsertOpenAlertAsync(InventoryStockAlert alert, CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(long alertId, CancellationToken cancellationToken = default);
    Task AcknowledgeAllOpenAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryStockAlert>> GetOpenAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryStockAlert>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default);
    Task ClearStaleOpenAlertsAsync(IReadOnlyCollection<string> stillActiveKeys, CancellationToken cancellationToken = default);
}

public interface IPurchaseOrderRepository
{
    Task<long> CreateAsync(PurchaseOrder order, IReadOnlyList<PurchaseOrderLine> lines, CancellationToken cancellationToken = default);
    Task MarkExportedAsync(long poId, CancellationToken cancellationToken = default);
    Task UpdateStatusAsync(long poId, string status, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PurchaseOrder>> GetRecentAsync(int take = 50, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PurchaseOrder>> GetReceivableAsync(CancellationToken cancellationToken = default);
    Task<PurchaseOrder?> GetByIdAsync(long poId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PurchaseOrderLine>> GetLinesAsync(long poId, CancellationToken cancellationToken = default);
}

public sealed class InventorySupplierRepository : IInventorySupplierRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public InventorySupplierRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<InventorySupplier>> GetAllAsync(
        bool activeOnly = true,
        CancellationToken cancellationToken = default)
    {
        var sql = activeOnly
            ? """
              SELECT SupplierCode, SupplierName, ContactEmail, Phone, Notes, IsActive, CreatedAtUtc
              FROM dbo.InventorySuppliers
              WHERE IsActive = 1
              ORDER BY SupplierName;
              """
            : """
              SELECT SupplierCode, SupplierName, ContactEmail, Phone, Notes, IsActive, CreatedAtUtc
              FROM dbo.InventorySuppliers
              ORDER BY SupplierName;
              """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<InventorySupplier>(
                new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task UpsertAsync(InventorySupplier supplier, CancellationToken cancellationToken = default)
    {
        const string sql = """
            MERGE dbo.InventorySuppliers AS target
            USING (SELECT @SupplierCode AS SupplierCode) AS source
            ON target.SupplierCode = source.SupplierCode
            WHEN MATCHED THEN
                UPDATE SET
                    SupplierName = @SupplierName,
                    ContactEmail = @ContactEmail,
                    Phone = @Phone,
                    Notes = @Notes,
                    IsActive = @IsActive
            WHEN NOT MATCHED THEN
                INSERT (SupplierCode, SupplierName, ContactEmail, Phone, Notes, IsActive)
                VALUES (@SupplierCode, @SupplierName, @ContactEmail, @Phone, @Notes, @IsActive);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, supplier, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}

public sealed class InventoryStockAlertRepository : IInventoryStockAlertRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public InventoryStockAlertRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task UpsertOpenAlertAsync(InventoryStockAlert alert, CancellationToken cancellationToken = default)
    {
        const string sql = """
            MERGE dbo.InventoryStockAlerts AS target
            USING (
                SELECT @ProductCode AS ProductCode, @AlertType AS AlertType
            ) AS source
            ON target.ProductCode = source.ProductCode
               AND target.AlertType = source.AlertType
               AND target.IsAcknowledged = 0
            WHEN MATCHED THEN
                UPDATE SET
                    ProductName = @ProductName,
                    Severity = @Severity,
                    StockQuantity = @StockQuantity,
                    ThresholdQty = @ThresholdQty,
                    AverageDailySales = @AverageDailySales,
                    SupplierCode = @SupplierCode,
                    Message = @Message,
                    ShiftId = @ShiftId,
                    CreatedAtUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (ProductCode, ProductName, AlertType, Severity, StockQuantity, ThresholdQty,
                        AverageDailySales, SupplierCode, Message, IsAcknowledged, ShiftId)
                VALUES (@ProductCode, @ProductName, @AlertType, @Severity, @StockQuantity, @ThresholdQty,
                        @AverageDailySales, @SupplierCode, @Message, 0, @ShiftId);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, alert, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task AcknowledgeAsync(long alertId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.InventoryStockAlerts
            SET IsAcknowledged = 1, AcknowledgedAtUtc = SYSUTCDATETIME()
            WHERE AlertId = @AlertId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(sql, new { AlertId = alertId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task AcknowledgeAllOpenAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.InventoryStockAlerts
            SET IsAcknowledged = 1, AcknowledgedAtUtc = SYSUTCDATETIME()
            WHERE IsAcknowledged = 0;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InventoryStockAlert>> GetOpenAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT AlertId, ProductCode, ProductName, AlertType, Severity, StockQuantity, ThresholdQty,
                   AverageDailySales, SupplierCode, Message, IsAcknowledged, ShiftId, CreatedAtUtc, AcknowledgedAtUtc
            FROM dbo.InventoryStockAlerts
            WHERE IsAcknowledged = 0
            ORDER BY
                CASE Severity WHEN 'Critical' THEN 0 WHEN 'Warning' THEN 1 ELSE 2 END,
                CreatedAtUtc DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<InventoryStockAlert>(
                new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<InventoryStockAlert>> GetRecentAsync(
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                   AlertId, ProductCode, ProductName, AlertType, Severity, StockQuantity, ThresholdQty,
                   AverageDailySales, SupplierCode, Message, IsAcknowledged, ShiftId, CreatedAtUtc, AcknowledgedAtUtc
            FROM dbo.InventoryStockAlerts
            ORDER BY CreatedAtUtc DESC, AlertId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<InventoryStockAlert>(
                new CommandDefinition(sql, new { Take = Math.Clamp(take, 1, 500) }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task ClearStaleOpenAlertsAsync(
        IReadOnlyCollection<string> stillActiveKeys,
        CancellationToken cancellationToken = default)
    {
        // Keys are "ProductCode|AlertType". Acknowledge open alerts no longer present in the latest scan.
        const string sql = """
            SELECT AlertId, ProductCode, AlertType
            FROM dbo.InventoryStockAlerts
            WHERE IsAcknowledged = 0;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var open = (await connection.QueryAsync<(long AlertId, string ProductCode, string AlertType)>(
                new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false)).AsList();

        var active = new HashSet<string>(stillActiveKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var row in open)
        {
            var key = $"{row.ProductCode}|{row.AlertType}";
            if (active.Contains(key))
            {
                continue;
            }

            await AcknowledgeAsync(row.AlertId, cancellationToken).ConfigureAwait(false);
        }
    }
}

public sealed class PurchaseOrderRepository : IPurchaseOrderRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public PurchaseOrderRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<long> CreateAsync(
        PurchaseOrder order,
        IReadOnlyList<PurchaseOrderLine> lines,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(lines);

        const string insertPo = """
            INSERT INTO dbo.PurchaseOrders
                (PoNumber, SupplierCode, SupplierName, Status, LineCount, TotalQuantity, TotalEstimatedCost,
                 OperatorUsername, Notes, SummaryText)
            OUTPUT INSERTED.PoId
            VALUES
                (@PoNumber, @SupplierCode, @SupplierName, @Status, @LineCount, @TotalQuantity, @TotalEstimatedCost,
                 @OperatorUsername, @Notes, @SummaryText);
            """;

        const string insertLine = """
            INSERT INTO dbo.PurchaseOrderLines
                (PoId, ProductCode, ProductName, CurrentStock, MinReorderQty, MaxStockCapacity,
                 AverageDailySales, SuggestedQty, UnitCost, LineTotal)
            VALUES
                (@PoId, @ProductCode, @ProductName, @CurrentStock, @MinReorderQty, @MaxStockCapacity,
                 @AverageDailySales, @SuggestedQty, @UnitCost, @LineTotal);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var poId = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(insertPo, order, tx, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        foreach (var line in lines)
        {
            line.PoId = poId;
            await connection.ExecuteAsync(
                    new CommandDefinition(insertLine, line, tx, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return poId;
    }

    public async Task MarkExportedAsync(long poId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.PurchaseOrders
            SET Status = @Status, ExportedAtUtc = SYSUTCDATETIME()
            WHERE PoId = @PoId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new { PoId = poId, Status = "Exported" },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateStatusAsync(long poId, string status, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.PurchaseOrders
            SET Status = @Status
            WHERE PoId = @PoId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(sql, new { PoId = poId, Status = status }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PurchaseOrder>> GetRecentAsync(
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                PoId, PoNumber, SupplierCode, SupplierName, Status, LineCount, TotalQuantity,
                TotalEstimatedCost, OperatorUsername, Notes, SummaryText, GeneratedAtUtc, ExportedAtUtc
            FROM dbo.PurchaseOrders
            ORDER BY GeneratedAtUtc DESC, PoId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<PurchaseOrder>(
                new CommandDefinition(sql, new { Take = Math.Clamp(take, 1, 200) }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PurchaseOrder>> GetReceivableAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT PoId, PoNumber, SupplierCode, SupplierName, Status, LineCount, TotalQuantity,
                   TotalEstimatedCost, OperatorUsername, Notes, SummaryText, GeneratedAtUtc, ExportedAtUtc
            FROM dbo.PurchaseOrders
            WHERE Status IN ('ReadyForSignOff', 'Exported', 'PartiallyReceived')
            ORDER BY GeneratedAtUtc DESC, PoId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<PurchaseOrder>(
                new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<PurchaseOrder?> GetByIdAsync(long poId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT PoId, PoNumber, SupplierCode, SupplierName, Status, LineCount, TotalQuantity,
                   TotalEstimatedCost, OperatorUsername, Notes, SummaryText, GeneratedAtUtc, ExportedAtUtc
            FROM dbo.PurchaseOrders
            WHERE PoId = @PoId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PurchaseOrder>(
                new CommandDefinition(sql, new { PoId = poId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PurchaseOrderLine>> GetLinesAsync(
        long poId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT PoLineId, PoId, ProductCode, ProductName, CurrentStock, MinReorderQty, MaxStockCapacity,
                   AverageDailySales, SuggestedQty, UnitCost, LineTotal
            FROM dbo.PurchaseOrderLines
            WHERE PoId = @PoId
            ORDER BY PoLineId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<PurchaseOrderLine>(
                new CommandDefinition(sql, new { PoId = poId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}
