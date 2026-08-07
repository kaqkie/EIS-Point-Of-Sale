/*
    Phase 35 — First-run setup wizard flags + statutory VAT seed
    Run: sqlcmd -S .\SQLEXPRESS -E -d PointOfSale -i Scripts\022_FirstRunSetupWizard.sql
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

MERGE dbo.Configurations AS target
USING (SELECT N'FirstRun.SetupWizardAvailable' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'FirstRun.SetupWizardAvailable', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Fiscal.StandardVatRatePercent' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Fiscal.StandardVatRatePercent', N'17.5', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Phase35Applied' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Phase35Applied', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Version' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN MATCHED THEN
    UPDATE SET ConfigJson = N'30', UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Version', N'30', GETUTCDATE());
GO

PRINT N'Phase 35 first-run setup schema flags applied.';
GO
