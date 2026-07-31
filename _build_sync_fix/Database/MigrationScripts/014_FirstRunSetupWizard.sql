/*
    Phase 35 — First-run setup wizard configuration flags
*/
SET NOCOUNT ON;
GO

MERGE dbo.Configurations AS target
USING (SELECT N'FirstRun.SetupWizardAvailable' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'FirstRun.SetupWizardAvailable', N'true', GETUTCDATE());
GO

PRINT N'Phase 35 MigrationScripts\014 applied.';
GO
