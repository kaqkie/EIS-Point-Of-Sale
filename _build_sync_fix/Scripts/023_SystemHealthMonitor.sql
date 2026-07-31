/*
    Phase 36 — System health monitor configuration flags
    Run: sqlcmd -S .\SQLEXPRESS -E -d PointOfSale -i Scripts\023_SystemHealthMonitor.sql
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Health.MonitorEnabled' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Health.MonitorEnabled', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Health.QueueBacklogWarnCount' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Health.QueueBacklogWarnCount', N'25', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Phase36Applied' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Phase36Applied', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Version' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN MATCHED THEN
    UPDATE SET ConfigJson = N'31', UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Version', N'31', GETUTCDATE());
GO

PRINT N'Phase 36 system health monitor schema flags applied.';
GO
