USE PointOfSale;
GO

IF COL_LENGTH(N'dbo.OfflineInvoiceQueue', N'FiscalResponseJson') IS NULL
BEGIN
    ALTER TABLE dbo.OfflineInvoiceQueue ADD FiscalResponseJson NVARCHAR(MAX) NULL;
END
GO
