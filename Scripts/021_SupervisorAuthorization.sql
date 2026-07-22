/*
    Phase 34 — Supervisor PIN columns + override framework flags
    Run: sqlcmd -S .\SQLEXPRESS -E -d PointOfSale -i Scripts\021_SupervisorAuthorization.sql
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.Operators', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.Operators', N'SupervisorPinHash') IS NULL
BEGIN
    ALTER TABLE dbo.Operators ADD
        SupervisorPinHash       NVARCHAR(200)   NULL,
        SupervisorPinSalt       NVARCHAR(200)   NULL,
        SupervisorPinIterations INT             NOT NULL
            CONSTRAINT DF_Operators_SupervisorPinIterations DEFAULT (0);
END
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Supervisor.DefaultPinSeeded' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Supervisor.DefaultPinSeeded', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Phase34Applied' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Phase34Applied', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Version' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN MATCHED THEN
    UPDATE SET ConfigJson = N'29', UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Version', N'29', GETUTCDATE());
GO

PRINT N'Phase 34 supervisor authorization schema applied. Default PIN is seeded by InitialDataSeeder.';
GO
