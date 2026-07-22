/*
    Albert Retail Terminal — Phase 37 InitialSetup
    Canonical SQL Express / LocalDB bootstrap companion for first-run.

    Reserved keywords (e.g. TRIGGER) are always delimited with [brackets].
    Batches are separated with explicit GO so sqlcmd / SSMS parse cleanly.

    Run (SQL Express):
      sqlcmd -S .\SQLEXPRESS -E -i Database\Scripts\InitialSetup.sql

    Run (LocalDB):
      sqlcmd -S "(localdb)\MSSQLLocalDB" -E -i Database\Scripts\InitialSetup.sql
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF DB_ID(N'PointOfSale') IS NULL
BEGIN
    CREATE DATABASE PointOfSale;
END
GO

USE PointOfSale;
GO

/* -------------------------------------------------------------------------
   Core tables
   ------------------------------------------------------------------------- */
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
        SecretKey       NVARCHAR(MAX)   NULL,
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
        [Status]        VARCHAR(20)     NOT NULL
            CONSTRAINT CK_OfflineInvoiceQueue_Status
            CHECK ([Status] IN (N'PENDING', N'SYNCING', N'SYNCED', N'QUARANTINED')),
        RetryCount      INT             NOT NULL CONSTRAINT DF_OfflineInvoiceQueue_RetryCount DEFAULT (0),
        NextRetryTime   DATETIME        NULL,
        ErrorMessage    NVARCHAR(MAX)   NULL,
        FiscalResponseJson NVARCHAR(MAX) NULL,
        CONSTRAINT PK_OfflineInvoiceQueue PRIMARY KEY CLUSTERED (Id)
    );

    CREATE INDEX IX_OfflineInvoiceQueue_PendingFifo
        ON dbo.OfflineInvoiceQueue ([Status], CreatedAt, Id)
        WHERE [Status] = N'PENDING';
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
        TaxRateId       VARCHAR(20)     NULL,
        CONSTRAINT PK_LocalInventory PRIMARY KEY CLUSTERED (ProductId)
    );

    CREATE UNIQUE INDEX UX_LocalInventory_ProductCode ON dbo.LocalInventory (ProductCode);
END
GO

/* -------------------------------------------------------------------------
   DatabaseBackupHistory — [Trigger] is a T-SQL reserved keyword; always quote.
   ------------------------------------------------------------------------- */
IF OBJECT_ID(N'dbo.DatabaseBackupHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DatabaseBackupHistory
    (
        BackupId        BIGINT          IDENTITY(1,1) NOT NULL,
        CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_DatabaseBackupHistory_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        [Trigger]       VARCHAR(40)     NOT NULL,
        BackupFilePath  NVARCHAR(500)   NOT NULL,
        Sha256Checksum  VARCHAR(64)     NOT NULL CONSTRAINT DF_DatabaseBackupHistory_Sha DEFAULT (N''),
        BackupBytes     BIGINT          NOT NULL CONSTRAINT DF_DatabaseBackupHistory_Bytes DEFAULT (0),
        Success         BIT             NOT NULL,
        ErrorMessage    NVARCHAR(2000)  NULL,
        VerifiedSha256  BIT             NOT NULL CONSTRAINT DF_DatabaseBackupHistory_VerifiedSha256 DEFAULT (0),
        CONSTRAINT PK_DatabaseBackupHistory PRIMARY KEY CLUSTERED (BackupId)
    );

    CREATE INDEX IX_DatabaseBackupHistory_CreatedAtUtc
        ON dbo.DatabaseBackupHistory (CreatedAtUtc DESC, BackupId DESC);
END
ELSE IF COL_LENGTH(N'dbo.DatabaseBackupHistory', N'VerifiedSha256') IS NULL
BEGIN
    ALTER TABLE dbo.DatabaseBackupHistory
        ADD VerifiedSha256 BIT NOT NULL
            CONSTRAINT DF_DatabaseBackupHistory_VerifiedSha256 DEFAULT (0);
END
GO

/* -------------------------------------------------------------------------
   Phase 37 schema markers
   ------------------------------------------------------------------------- */
MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Phase37Applied' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Phase37Applied', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Version' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN MATCHED THEN
    UPDATE SET ConfigJson = N'32', UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Version', N'32', GETUTCDATE());
GO

PRINT N'Phase 37 InitialSetup applied (reserved keywords delimited; GO batches separated).';
GO
