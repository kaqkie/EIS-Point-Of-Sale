/*
    Albert Retail Terminal — production maintenance (Phase 7)
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\003_ProductionMaintenance.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF OBJECT_ID(N'dbo.MraApiAuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MraApiAuditLog
    (
        AuditId         BIGINT          IDENTITY(1,1) NOT NULL,
        CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_MraApiAuditLog_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        HttpMethod      NVARCHAR(10)    NOT NULL,
        RequestPath     NVARCHAR(500)   NOT NULL,
        HttpStatusCode  INT             NULL,
        DurationMs      INT             NOT NULL,
        RequestBody     NVARCHAR(MAX)   NULL,
        ResponseBody    NVARCHAR(MAX)   NULL,
        IsSuccess       BIT             NOT NULL,
        ErrorMessage    NVARCHAR(2000)  NULL,
        CONSTRAINT PK_MraApiAuditLog PRIMARY KEY CLUSTERED (AuditId)
    );

    CREATE INDEX IX_MraApiAuditLog_CreatedAtUtc ON dbo.MraApiAuditLog (CreatedAtUtc DESC);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_OfflineInvoiceQueue_Status_CreatedAt'
      AND object_id = OBJECT_ID(N'dbo.OfflineInvoiceQueue'))
BEGIN
    CREATE INDEX IX_OfflineInvoiceQueue_Status_CreatedAt
        ON dbo.OfflineInvoiceQueue (Status, CreatedAt ASC, Id ASC)
        INCLUDE (RetryCount, NextRetryTime, ErrorMessage);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_LocalInventory_ProductCode'
      AND object_id = OBJECT_ID(N'dbo.LocalInventory'))
BEGIN
    CREATE INDEX IX_LocalInventory_ProductCode
        ON dbo.LocalInventory (ProductCode)
        INCLUDE (Name, UnitPrice, StockQuantity, TaxRateId, HsCode, UnitOfMeasure);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_LocalInventory_ProductId_Covering'
      AND object_id = OBJECT_ID(N'dbo.LocalInventory'))
BEGIN
    CREATE INDEX IX_LocalInventory_ProductId_Covering
        ON dbo.LocalInventory (ProductId)
        INCLUDE (ProductCode, Name, StockQuantity, UnitPrice);
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_CleanupSyncedOfflineInvoices
    @RetentionDays INT = 90
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @CutoffUtc DATETIME2(7) = DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME());

    DELETE FROM dbo.OfflineInvoiceQueue
    WHERE Status = N'SYNCED'
      AND CreatedAt < @CutoffUtc;

    SELECT @@ROWCOUNT AS DeletedRows, @CutoffUtc AS CutoffUtc;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_CleanupMraApiAuditLog
    @RetentionDays INT = 90
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @CutoffUtc DATETIME2(7) = DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME());

    DELETE FROM dbo.MraApiAuditLog
    WHERE CreatedAtUtc < @CutoffUtc;

    SELECT @@ROWCOUNT AS DeletedRows, @CutoffUtc AS CutoffUtc;
END
GO

PRINT N'Production maintenance objects applied.';
GO
