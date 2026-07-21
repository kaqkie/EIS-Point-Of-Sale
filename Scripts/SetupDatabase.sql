/*
    Point of Sale — SQL Server Express setup (Phase 1)
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\SetupDatabase.sql
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_ID(N'PointOfSale') IS NULL
BEGIN
    CREATE DATABASE PointOfSale;
END
GO

USE PointOfSale;
GO

IF OBJECT_ID(N'dbo.Terminals', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Terminals
    (
        TerminalId      VARCHAR(50)     NOT NULL,
        BranchCode      VARCHAR(50)     NULL,
        ActivationState VARCHAR(20)     NOT NULL
            CONSTRAINT CK_Terminals_ActivationState
            CHECK (ActivationState IN (
                N'NotActivated',
                N'PendingConfirmation',
                N'Activated',
                N'Deactivated')),
        SecretKey       NVARCHAR(MAX)   NULL,  -- DPAPI-protected Base64 ciphertext (application layer)
        LastSyncedAt    DATETIME        NULL,
        CONSTRAINT PK_Terminals PRIMARY KEY CLUSTERED (TerminalId)
    );
END
GO

IF OBJECT_ID(N'dbo.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Configurations
    (
        ConfigKey   VARCHAR(100)    NOT NULL,
        ConfigJson  NVARCHAR(MAX)   NOT NULL,
        UpdatedAt   DATETIME        NOT NULL CONSTRAINT DF_Configurations_UpdatedAt DEFAULT (GETUTCDATE()),
        CONSTRAINT PK_Configurations PRIMARY KEY CLUSTERED (ConfigKey)
    );
END
GO

IF OBJECT_ID(N'dbo.OfflineInvoiceQueue', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.OfflineInvoiceQueue
    (
        Id              INT             IDENTITY(1,1) NOT NULL,
        PayloadJson     NVARCHAR(MAX)   NOT NULL,
        CreatedAt       DATETIME        NOT NULL CONSTRAINT DF_OfflineInvoiceQueue_CreatedAt DEFAULT (GETUTCDATE()),
        Status          VARCHAR(20)     NOT NULL
            CONSTRAINT CK_OfflineInvoiceQueue_Status
            CHECK (Status IN (N'PENDING', N'SYNCING', N'SYNCED', N'QUARANTINED')),
        RetryCount      INT             NOT NULL CONSTRAINT DF_OfflineInvoiceQueue_RetryCount DEFAULT (0),
        NextRetryTime   DATETIME        NULL,
        ErrorMessage    NVARCHAR(MAX)   NULL,
        CONSTRAINT PK_OfflineInvoiceQueue PRIMARY KEY CLUSTERED (Id)
    );

    CREATE INDEX IX_OfflineInvoiceQueue_PendingFifo
        ON dbo.OfflineInvoiceQueue (Status, CreatedAt, Id)
        WHERE Status = N'PENDING';
END
GO

IF OBJECT_ID(N'dbo.LocalInventory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LocalInventory
    (
        ProductId       VARCHAR(50)     NOT NULL,
        ProductCode     VARCHAR(100)    NOT NULL,
        Name            NVARCHAR(200)   NOT NULL,
        UnitPrice       DECIMAL(18, 2)  NOT NULL,
        StockQuantity   DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_LocalInventory_Stock DEFAULT (0),
        HsCode          VARCHAR(50)     NULL,
        UnitOfMeasure   VARCHAR(20)     NULL,
        CONSTRAINT PK_LocalInventory PRIMARY KEY CLUSTERED (ProductId)
    );

    CREATE UNIQUE INDEX UX_LocalInventory_ProductCode ON dbo.LocalInventory (ProductCode);
END
GO

PRINT N'PointOfSale database schema applied.';
GO
