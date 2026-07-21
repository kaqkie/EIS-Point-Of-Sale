using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface ILocalInventoryRepository
{
    Task<IReadOnlyList<LocalInventoryItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<LocalInventoryItem?> GetByProductCodeAsync(string productCode, CancellationToken cancellationToken = default);
    Task UpsertAsync(LocalInventoryItem item, CancellationToken cancellationToken = default);
}

public sealed class LocalInventoryRepository : ILocalInventoryRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public LocalInventoryRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<LocalInventoryItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId
            FROM dbo.LocalInventory
            ORDER BY Name;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<LocalInventoryItem>(
            new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<LocalInventoryItem?> GetByProductCodeAsync(
        string productCode,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId
            FROM dbo.LocalInventory
            WHERE ProductCode = @ProductCode;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<LocalInventoryItem>(
            new CommandDefinition(sql, new { ProductCode = productCode }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpsertAsync(LocalInventoryItem item, CancellationToken cancellationToken = default)
    {
        const string sql = """
            MERGE dbo.LocalInventory AS target
            USING (SELECT @ProductId AS ProductId) AS source
            ON target.ProductId = source.ProductId
            WHEN MATCHED THEN
                UPDATE SET
                    ProductCode = @ProductCode,
                    Name = @Name,
                    UnitPrice = @UnitPrice,
                    StockQuantity = @StockQuantity,
                    HsCode = @HsCode,
                    UnitOfMeasure = @UnitOfMeasure,
                    TaxRateId = @TaxRateId
            WHEN NOT MATCHED THEN
                INSERT (ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId)
                VALUES (@ProductId, @ProductCode, @Name, @UnitPrice, @StockQuantity, @HsCode, @UnitOfMeasure, @TaxRateId);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, item, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
