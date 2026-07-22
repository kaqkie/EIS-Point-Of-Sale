/*
    Albert Retail Terminal — Phase 28 performance index optimization
    Run: sqlcmd -S .\SQLEXPRESS -E -d PointOfSale -i Database\MigrationScripts\007_PerformanceIndexOptimization.sql

    Resolves filtered-index collation mismatches (VARCHAR Status vs NVARCHAR literals)
    and adds covering indexes for MRA queue polling and sales reporting.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* ── SalesTransactions (local fiscal sales ledger for reporting) ───────────── */
IF OBJECT_ID(N'dbo.SalesTransactions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SalesTransactions
    (
        TransactionId       BIGINT          IDENTITY(1,1) NOT NULL,
        QueueId             INT             NULL,
        BusinessDate        DATE            NOT NULL,
        InvoiceNumber       VARCHAR(50)     NOT NULL,
        PaymentMethod       VARCHAR(20)     NOT NULL,
        GrossAmountMwk      DECIMAL(18, 2)  NOT NULL,
        VatAmountMwk        DECIMAL(18, 2)  NOT NULL,
        ShiftId             INT             NULL,
        SyncStatus          VARCHAR(20)     NOT NULL
            CONSTRAINT CK_SalesTransactions_SyncStatus
            CHECK (SyncStatus IN ('PENDING', 'SYNCING', 'SYNCED', 'QUARANTINED')),
        CreatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_SalesTransactions_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_SalesTransactions PRIMARY KEY CLUSTERED (TransactionId)
    );

    CREATE UNIQUE INDEX UX_SalesTransactions_InvoiceNumber
        ON dbo.SalesTransactions (InvoiceNumber);

    CREATE INDEX IX_SalesTransactions_BusinessDate_Reporting
        ON dbo.SalesTransactions (BusinessDate DESC, PaymentMethod, TransactionId)
        INCLUDE (GrossAmountMwk, VatAmountMwk, SyncStatus, ShiftId);

    CREATE INDEX IX_SalesTransactions_CreatedAtUtc
        ON dbo.SalesTransactions (CreatedAtUtc DESC, TransactionId DESC);

    CREATE INDEX IX_SalesTransactions_QueueId
        ON dbo.SalesTransactions (QueueId)
        WHERE QueueId IS NOT NULL;
END
GO

/* Drop filtered queue index that conflicts with VARCHAR Status collation */
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_OfflineInvoiceQueue_PendingFifo'
      AND object_id = OBJECT_ID(N'dbo.OfflineInvoiceQueue'))
BEGIN
    DROP INDEX IX_OfflineInvoiceQueue_PendingFifo ON dbo.OfflineInvoiceQueue;
END
GO

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_OfflineInvoiceQueue_Status_CreatedAt'
      AND object_id = OBJECT_ID(N'dbo.OfflineInvoiceQueue'))
BEGIN
    DROP INDEX IX_OfflineInvoiceQueue_Status_CreatedAt ON dbo.OfflineInvoiceQueue;
END
GO

/* MRA sync FIFO poll — non-filtered, VARCHAR-safe */
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_OfflineInvoiceQueue_MraSyncPoll'
      AND object_id = OBJECT_ID(N'dbo.OfflineInvoiceQueue'))
BEGIN
    CREATE INDEX IX_OfflineInvoiceQueue_MraSyncPoll
        ON dbo.OfflineInvoiceQueue (Status ASC, NextRetryTime ASC, CreatedAt ASC, Id ASC)
        INCLUDE (RetryCount, ErrorMessage, PayloadJson);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_OfflineInvoiceQueue_SyncedReporting'
      AND object_id = OBJECT_ID(N'dbo.OfflineInvoiceQueue'))
BEGIN
    CREATE INDEX IX_OfflineInvoiceQueue_SyncedReporting
        ON dbo.OfflineInvoiceQueue (Status ASC, CreatedAt DESC, Id DESC)
        INCLUDE (FiscalResponseJson, PayloadJson);
END
GO

/* LocalInventory — align ProductCode index collation with column (VARCHAR, database default) */
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UX_LocalInventory_ProductCode'
      AND object_id = OBJECT_ID(N'dbo.LocalInventory'))
BEGIN
    DROP INDEX UX_LocalInventory_ProductCode ON dbo.LocalInventory;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UX_LocalInventory_ProductCode'
      AND object_id = OBJECT_ID(N'dbo.LocalInventory'))
BEGIN
    CREATE UNIQUE INDEX UX_LocalInventory_ProductCode
        ON dbo.LocalInventory (ProductCode)
        INCLUDE (Name, UnitPrice, StockQuantity, TaxRateId, HsCode, UnitOfMeasure);
END
GO

IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_LocalInventory_ProductCode'
      AND object_id = OBJECT_ID(N'dbo.LocalInventory'))
BEGIN
    DROP INDEX IX_LocalInventory_ProductCode ON dbo.LocalInventory;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_LocalInventory_ProductId_Covering'
      AND object_id = OBJECT_ID(N'dbo.LocalInventory'))
BEGIN
    CREATE INDEX IX_LocalInventory_ProductId_Covering
        ON dbo.LocalInventory (ProductId)
        INCLUDE (ProductCode, Name, StockQuantity, UnitPrice, TaxRateId);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_LocalInventory_StockQuantity'
      AND object_id = OBJECT_ID(N'dbo.LocalInventory'))
BEGIN
    CREATE INDEX IX_LocalInventory_StockQuantity
        ON dbo.LocalInventory (StockQuantity ASC, ProductCode ASC)
        INCLUDE (Name, UnitPrice);
END
GO

/* Maintenance audit log */
IF OBJECT_ID(N'dbo.DatabaseMaintenanceLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DatabaseMaintenanceLog
    (
        LogId           BIGINT          IDENTITY(1,1) NOT NULL,
        ExecutedAtUtc   DATETIME2(7)    NOT NULL CONSTRAINT DF_DatabaseMaintenanceLog_ExecutedAtUtc DEFAULT (SYSUTCDATETIME()),
        Operation       VARCHAR(40)     NOT NULL,
        Success         BIT             NOT NULL,
        Detail          NVARCHAR(2000)  NULL,
        DurationMs      INT             NOT NULL CONSTRAINT DF_DatabaseMaintenanceLog_DurationMs DEFAULT (0),
        FragmentedIndexesBefore INT     NULL,
        DatabaseSizeMbBefore    BIGINT  NULL,
        CONSTRAINT PK_DatabaseMaintenanceLog PRIMARY KEY CLUSTERED (LogId)
    );

    CREATE INDEX IX_DatabaseMaintenanceLog_ExecutedAtUtc
        ON dbo.DatabaseMaintenanceLog (ExecutedAtUtc DESC, LogId DESC);
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_PurgeExpiredDiagnosticTelemetry
    @RetentionDays INT
AS
BEGIN
    SET NOCOUNT ON;
    IF @RetentionDays < 1 SET @RetentionDays = 1;

    DECLARE @Cutoff DATETIME2(7) = DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME());

    DELETE FROM dbo.DiagnosticTelemetryEvents
    WHERE CreatedAtUtc < @Cutoff;

    SELECT @@ROWCOUNT AS DeletedRows, @Cutoff AS CutoffUtc;
END
GO

PRINT N'Phase 28 performance index optimization applied.';
GO
