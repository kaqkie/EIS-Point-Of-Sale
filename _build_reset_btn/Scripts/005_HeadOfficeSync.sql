/*
    Albert Retail Terminal — Phase 13 head-office sync & catalog replication
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\005_HeadOfficeSync.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF OBJECT_ID(N'dbo.HeadOfficeSyncOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.HeadOfficeSyncOutbox
    (
        OutboxId        BIGINT          IDENTITY(1,1) NOT NULL,
        PayloadType     VARCHAR(40)     NOT NULL,
        CorrelationKey  NVARCHAR(200)   NOT NULL,
        PlainJson       NVARCHAR(MAX)   NOT NULL,
        Status          VARCHAR(20)     NOT NULL
            CONSTRAINT CK_HeadOfficeSyncOutbox_Status
            CHECK (Status IN (N'Pending', N'Uploading', N'Uploaded', N'Failed')),
        CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_HeadOfficeSyncOutbox_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UploadedAtUtc   DATETIME2(7)    NULL,
        ErrorMessage    NVARCHAR(2000)  NULL,
        AttemptCount    INT             NOT NULL CONSTRAINT DF_HeadOfficeSyncOutbox_AttemptCount DEFAULT (0),
        CONSTRAINT PK_HeadOfficeSyncOutbox PRIMARY KEY CLUSTERED (OutboxId)
    );

    CREATE INDEX IX_HeadOfficeSyncOutbox_Status_Created
        ON dbo.HeadOfficeSyncOutbox (Status, CreatedAtUtc, OutboxId);

    CREATE UNIQUE INDEX UX_HeadOfficeSyncOutbox_Type_Correlation_Active
        ON dbo.HeadOfficeSyncOutbox (PayloadType, CorrelationKey)
        WHERE Status IN (N'Pending', N'Uploading', N'Uploaded');
END
GO

IF COL_LENGTH(N'dbo.LocalInventory', N'CatalogSource') IS NULL
    ALTER TABLE dbo.LocalInventory ADD CatalogSource VARCHAR(20) NOT NULL
        CONSTRAINT DF_LocalInventory_CatalogSource DEFAULT (N'Local');
GO

IF COL_LENGTH(N'dbo.LocalInventory', N'HeadOfficeRevisionUtc') IS NULL
    ALTER TABLE dbo.LocalInventory ADD HeadOfficeRevisionUtc DATETIME2(7) NULL;
GO

IF COL_LENGTH(N'dbo.LocalInventory', N'LastReplicatedAtUtc') IS NULL
    ALTER TABLE dbo.LocalInventory ADD LastReplicatedAtUtc DATETIME2(7) NULL;
GO
