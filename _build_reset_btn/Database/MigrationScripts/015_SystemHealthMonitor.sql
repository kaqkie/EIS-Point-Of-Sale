/*
    Phase 36 — System health monitor flags
*/
SET NOCOUNT ON;
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Health.MonitorEnabled' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Health.MonitorEnabled', N'true', GETUTCDATE());
GO

PRINT N'Phase 36 MigrationScripts\015 applied.';
GO
