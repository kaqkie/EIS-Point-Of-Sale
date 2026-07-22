IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Catalog.DemoSeedApplied')
   AND NOT EXISTS (SELECT 1 FROM dbo.LocalInventory)
BEGIN
    INSERT INTO dbo.LocalInventory
    (
        ProductId, ProductCode, Name, UnitPrice, StockQuantity, HsCode, UnitOfMeasure, TaxRateId,
        CatalogSource, MinReorderQty, MaxStockCapacity, SupplierCode, SupplierName,
        AverageUnitCost, MarkupPercent
    )
    VALUES
    (N'ART-WATER-500', N'ART-WATER-500', N'Bottled Water 500ml', 350.00, 120, N'2201', N'EA', N'A', N'Local', 24, 240, N'SUP-LOCAL', N'Local Supplier', 250.00, 25.0000),
    (N'ART-BREAD-WHT', N'ART-BREAD-WHT', N'White Bread Loaf', 1500.00, 40, N'1905', N'EA', N'A', N'Local', 12, 80, N'SUP-BAKERY', N'Bakery Supply Co', 1100.00, 25.0000),
    (N'ART-SOAP-250', N'ART-SOAP-250', N'Bar Soap 250g', 800.00, 75, N'3401', N'EA', N'A', N'Local', 20, 150, N'SUP-LOCAL', N'Local Supplier', 580.00, 25.0000),
    (N'ART-RICE-2KG', N'ART-RICE-2KG', N'Rice 2kg', 4500.00, 60, N'1006', N'EA', N'A', N'Local', 15, 120, N'SUP-GROCERY', N'Grocery Wholesaler', 3400.00, 25.0000),
    (N'ART-OIL-1L', N'ART-OIL-1L', N'Cooking Oil 1L', 5200.00, 35, N'1507', N'EA', N'A', N'Local', 10, 80, N'SUP-GROCERY', N'Grocery Wholesaler', 4000.00, 25.0000),
    (N'ART-SUGAR-1KG', N'ART-SUGAR-1KG', N'Sugar 1kg', 2800.00, 50, N'1701', N'EA', N'A', N'Local', 12, 100, N'SUP-GROCERY', N'Grocery Wholesaler', 2100.00, 25.0000),
    (N'ART-MILK-1L', N'ART-MILK-1L', N'Fresh Milk 1L', 2200.00, 28, N'0401', N'EA', N'A', N'Local', 10, 60, N'SUP-DAIRY', N'Dairy Fresh', 1650.00, 25.0000),
    (N'ART-EGGS-12', N'ART-EGGS-12', N'Eggs (dozen)', 3500.00, 22, N'0407', N'EA', N'A', N'Local', 8, 48, N'SUP-DAIRY', N'Dairy Fresh', 2600.00, 25.0000);

    INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Catalog.DemoSeedApplied', N'true', GETUTCDATE());
END;

SELECT COUNT(*) AS InventoryCount FROM dbo.LocalInventory;
