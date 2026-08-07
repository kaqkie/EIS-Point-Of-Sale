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

    /// <summary>Deletes rows matching any of the given product codes. Returns deleted count.</summary>
    Task<int> DeleteByProductCodesAsync(
        IReadOnlyCollection<string> productCodes,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes rows with the given catalog source (e.g. Demo). Returns deleted count.</summary>
    Task<int> DeleteByCatalogSourceAsync(
        string catalogSource,
        CancellationToken cancellationToken = default);

    Task UpdateReorderSettingsAsync(
        string productCode,
        decimal minReorderQty,
        decimal maxStockCapacity,
        string? supplierCode,
        string? supplierName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a goods receipt: increases stock, updates weighted-average cost and optional retail price.
    /// </summary>
    Task ApplyGoodsReceiptAsync(
        string productCode,
        decimal goodQtyReceived,
        decimal unitCost,
        decimal newAverageUnitCost,
        decimal? newRetailPrice,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a head-office catalog row. Master fields always win; stock is preserved when
    /// <paramref name="preserveLocalStock"/> is true (active sales shift / transaction integrity).
    /// Local reorder thresholds and supplier assignment are preserved on match.
    /// </summary>
    Task ApplyHeadOfficeCatalogAsync(
        LocalInventoryItem catalogItem,
        bool preserveLocalStock,
        CancellationToken cancellationToken = default);
}

public sealed class LocalInventoryRepository : ILocalInventoryRepository
{
    private const string SelectColumns = """
        ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId,
        CatalogSource, HeadOfficeRevisionUtc, LastReplicatedAtUtc,
        MinReorderQty, MaxStockCapacity, SupplierCode, SupplierName, AverageUnitCost, MarkupPercent
        """;

    private readonly ISqlConnectionFactory _connectionFactory;

    public LocalInventoryRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<LocalInventoryItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT {SelectColumns}
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
        var sql = $"""
            SELECT {SelectColumns}
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
        var sql = $"""
            SELECT {SelectColumns}
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
                    LastReplicatedAtUtc = @LastReplicatedAtUtc,
                    MinReorderQty = @MinReorderQty,
                    MaxStockCapacity = @MaxStockCapacity,
                    SupplierCode = @SupplierCode,
                    SupplierName = @SupplierName,
                    AverageUnitCost = @AverageUnitCost,
                    MarkupPercent = @MarkupPercent
            WHEN NOT MATCHED THEN
                INSERT (ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId,
                        CatalogSource, HeadOfficeRevisionUtc, LastReplicatedAtUtc,
                        MinReorderQty, MaxStockCapacity, SupplierCode, SupplierName, AverageUnitCost, MarkupPercent)
                VALUES (@ProductId, @ProductCode, @Name, @UnitPrice, @StockQuantity, @HsCode, @UnitOfMeasure, @TaxRateId,
                        ISNULL(@CatalogSource, N'Local'), @HeadOfficeRevisionUtc, @LastReplicatedAtUtc,
                        @MinReorderQty, @MaxStockCapacity, @SupplierCode, @SupplierName, @AverageUnitCost, @MarkupPercent);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, item, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<int> DeleteByProductCodesAsync(
        IReadOnlyCollection<string> productCodes,
        CancellationToken cancellationToken = default)
    {
        if (productCodes is null || productCodes.Count == 0)
        {
            return 0;
        }

        var codes = productCodes
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (codes.Length == 0)
        {
            return 0;
        }

        const string sql = """
            DELETE FROM dbo.LocalInventory
            WHERE ProductCode IN @Codes;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteAsync(
                new CommandDefinition(sql, new { Codes = codes }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<int> DeleteByCatalogSourceAsync(
        string catalogSource,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(catalogSource))
        {
            return 0;
        }

        const string sql = """
            DELETE FROM dbo.LocalInventory
            WHERE CatalogSource = @CatalogSource;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new { CatalogSource = catalogSource.Trim() },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateReorderSettingsAsync(
        string productCode,
        decimal minReorderQty,
        decimal maxStockCapacity,
        string? supplierCode,
        string? supplierName,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.LocalInventory
            SET MinReorderQty = @MinReorderQty,
                MaxStockCapacity = @MaxStockCapacity,
                SupplierCode = @SupplierCode,
                SupplierName = @SupplierName
            WHERE ProductCode = @ProductCode;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new
                    {
                        ProductCode = productCode,
                        MinReorderQty = minReorderQty,
                        MaxStockCapacity = maxStockCapacity,
                        SupplierCode = supplierCode,
                        SupplierName = supplierName
                    },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task ApplyGoodsReceiptAsync(
        string productCode,
        decimal goodQtyReceived,
        decimal unitCost,
        decimal newAverageUnitCost,
        decimal? newRetailPrice,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.LocalInventory
            SET StockQuantity = StockQuantity + @GoodQty,
                AverageUnitCost = @NewAverageUnitCost,
                UnitPrice = COALESCE(@NewRetailPrice, UnitPrice)
            WHERE ProductCode = @ProductCode;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new
                    {
                        ProductCode = productCode,
                        GoodQty = goodQtyReceived,
                        NewAverageUnitCost = newAverageUnitCost,
                        NewRetailPrice = newRetailPrice
                    },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (rows == 0)
        {
            throw new InvalidOperationException($"Product '{productCode}' was not found for goods receipt.");
        }
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
                        CatalogSource, HeadOfficeRevisionUtc, LastReplicatedAtUtc,
                        MinReorderQty, MaxStockCapacity, SupplierCode, SupplierName)
                VALUES (@ProductId, @ProductCode, @Name, @UnitPrice, @StockQuantity, @HsCode, @UnitOfMeasure, @TaxRateId,
                        N'HeadOffice', @HeadOfficeRevisionUtc, SYSUTCDATETIME(),
                        @MinReorderQty, @MaxStockCapacity, @SupplierCode, @SupplierName);
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
                    catalogItem.MinReorderQty,
                    catalogItem.MaxStockCapacity,
                    catalogItem.SupplierCode,
                    catalogItem.SupplierName,
                    PreserveLocalStock = preserveLocalStock ? 1 : 0
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
