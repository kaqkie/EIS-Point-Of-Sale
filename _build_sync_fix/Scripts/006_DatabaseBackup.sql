/*
    Albert Retail Terminal — Phase 14 disaster recovery backup history
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\006_DatabaseBackup.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

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
        CONSTRAINT PK_DatabaseBackupHistory PRIMARY KEY CLUSTERED (BackupId)
    );

    CREATE INDEX IX_DatabaseBackupHistory_CreatedAtUtc
        ON dbo.DatabaseBackupHistory (CreatedAtUtc DESC, BackupId DESC);
END
GO
