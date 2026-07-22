/*
    Phase 33 — Automated end-of-day SQL Express backup + DR hardening
    Run: sqlcmd -S .\SQLEXPRESS -E -d PointOfSale -i Scripts\020_EndOfDayBackupAndHardwareHardening.sql
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
        Trigger         VARCHAR(40)     NOT NULL,
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

MERGE dbo.Configurations AS target
USING (SELECT N'Backup.EndOfDayHourLocal' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Backup.EndOfDayHourLocal', N'21', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Backup.RetentionDays' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Backup.RetentionDays', N'30', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Hardware.FaultToleranceEnabled' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Hardware.FaultToleranceEnabled', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Phase33Applied' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Phase33Applied', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Version' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN MATCHED THEN
    UPDATE SET ConfigJson = N'28', UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Version', N'28', GETUTCDATE());
GO

PRINT N'Phase 33 end-of-day backup / hardware hardening schema applied.';
GO
