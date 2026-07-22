/*
    Phase 37 — MigrationScripts reserved-keyword hardening for DatabaseBackupHistory.[Trigger]
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
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
        VerifiedSha256  BIT             NOT NULL CONSTRAINT DF_DatabaseBackupHistory_VerifiedSha256 DEFAULT (0),
        CONSTRAINT PK_DatabaseBackupHistory PRIMARY KEY CLUSTERED (BackupId)
    );

    CREATE INDEX IX_DatabaseBackupHistory_CreatedAtUtc
        ON dbo.DatabaseBackupHistory (CreatedAtUtc DESC, BackupId DESC);
END
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Phase37Applied' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Phase37Applied', N'true', GETUTCDATE());
GO

PRINT N'Phase 37 MigrationScripts\016 applied.';
GO
