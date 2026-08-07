/*
    Phase 33 — DatabaseBackupHistory integrity column + EOD config flags
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.DatabaseBackupHistory', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.DatabaseBackupHistory', N'VerifiedSha256') IS NULL
BEGIN
    ALTER TABLE dbo.DatabaseBackupHistory
        ADD VerifiedSha256 BIT NOT NULL
            CONSTRAINT DF_DatabaseBackupHistory_VerifiedSha256 DEFAULT (0);
END
GO

PRINT N'Phase 33 MigrationScripts\012 applied.';
GO
