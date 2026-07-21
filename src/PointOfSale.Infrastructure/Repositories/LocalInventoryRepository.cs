using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface ILocalInventoryRepository
{
    Task<IReadOnlyList<LocalInventoryItem>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<LocalInventoryItem?> GetByProductCodeAsync(string productCode, CancellationToken cancellationToken = default);
    Task<LocalInventoryItem?> GetByProductIdAsync(string productId, CancellationToken cancellationToken = default);
    Task UpsertAsync(LocalInventoryItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a head-office catalog row. Master fields always win; stock is preserved when
    /// <paramref name="preserveLocalStock"/> is true (active sales shift / transaction integrity).
    /// </summary>
    Task ApplyHeadOfficeCatalogAsync(
        LocalInventoryItem catalogItem,
        bool preserveLocalStock,
        CancellationToken cancellationToken = default);
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
            SELECT ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId,
                   CatalogSource, HeadOfficeRevisionUtc, LastReplicatedAtUtc
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
            SELECT ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId,
                   CatalogSource, HeadOfficeRevisionUtc, LastReplicatedAtUtc
            FROM dbo.LocalInventory
            WHERE ProductCode = @ProductCode;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<LocalInventoryItem>(
            new CommandDefinition(sql, new { ProductCode = productCode }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<LocalInventoryItem?> GetByProductIdAsync(
        string productId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId,
                   CatalogSource, HeadOfficeRevisionUtc, LastReplicatedAtUtc
            FROM dbo.LocalInventory
            WHERE ProductId = @ProductId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<LocalInventoryItem>(
            new CommandDefinition(sql, new { ProductId = productId }, cancellationToken: cancellationToken))
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
                    TaxRateId = @TaxRateId,
                    CatalogSource = ISNULL(@CatalogSource, target.CatalogSource),
                    HeadOfficeRevisionUtc = @HeadOfficeRevisionUtc,
                    LastReplicatedAtUtc = @LastReplicatedAtUtc
            WHEN NOT MATCHED THEN
                INSERT (ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId,
                        CatalogSource, HeadOfficeRevisionUtc, LastReplicatedAtUtc)
                VALUES (@ProductId, @ProductCode, @Name, @UnitPrice, @StockQuantity, @HsCode, @UnitOfMeasure, @TaxRateId,
                        ISNULL(@CatalogSource, N'Local'), @HeadOfficeRevisionUtc, @LastReplicatedAtUtc);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, item, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task ApplyHeadOfficeCatalogAsync(
        LocalInventoryItem catalogItem,
        bool preserveLocalStock,
        CancellationToken cancellationToken = default)
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
                    StockQuantity = CASE WHEN @PreserveLocalStock = 1 THEN target.StockQuantity ELSE @StockQuantity END,
                    HsCode = @HsCode,
                    UnitOfMeasure = @UnitOfMeasure,
                    TaxRateId = @TaxRateId,
                    CatalogSource = N'HeadOffice',
                    HeadOfficeRevisionUtc = @HeadOfficeRevisionUtc,
                    LastReplicatedAtUtc = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId,
                        CatalogSource, HeadOfficeRevisionUtc, LastReplicatedAtUtc)
                VALUES (@ProductId, @ProductCode, @Name, @UnitPrice, @StockQuantity, @HsCode, @UnitOfMeasure, @TaxRateId,
                        N'HeadOffice', @HeadOfficeRevisionUtc, SYSUTCDATETIME());
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    catalogItem.ProductId,
                    catalogItem.ProductCode,
                    catalogItem.Name,
                    catalogItem.UnitPrice,
                    catalogItem.StockQuantity,
                    catalogItem.HsCode,
                    catalogItem.UnitOfMeasure,
                    catalogItem.TaxRateId,
                    catalogItem.HeadOfficeRevisionUtc,
                    PreserveLocalStock = preserveLocalStock ? 1 : 0
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
