IF COL_LENGTH(N'dbo.LocalInventory', N'TaxRateId') IS NULL
BEGIN
    ALTER TABLE dbo.LocalInventory ADD TaxRateId VARCHAR(20) NULL;
END
GO
