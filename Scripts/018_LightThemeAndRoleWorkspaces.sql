/*
    Phase 31 — light UI workspace preferences + statutory VAT config key
    Run: sqlcmd -S .\SQLEXPRESS -E -d PointOfSale -i Scripts\018_LightThemeAndRoleWorkspaces.sql
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.UiWorkspacePreferences', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UiWorkspacePreferences
    (
        PreferenceId        INT             IDENTITY(1,1) NOT NULL,
        OperatorId          INT             NOT NULL,
        PreferredShell      VARCHAR(20)     NOT NULL CONSTRAINT DF_UiWorkspacePreferences_Shell DEFAULT (N'Cashier'),
        ThemeMode           VARCHAR(20)     NOT NULL CONSTRAINT DF_UiWorkspacePreferences_Theme DEFAULT (N'Light'),
        UpdatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_UiWorkspacePreferences_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_UiWorkspacePreferences PRIMARY KEY CLUSTERED (PreferenceId),
        CONSTRAINT UQ_UiWorkspacePreferences_Operator UNIQUE (OperatorId)
    );
END
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Fiscal.StandardVatRatePercent' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Fiscal.StandardVatRatePercent', N'17.5', GETUTCDATE())
WHEN MATCHED THEN
    UPDATE SET ConfigJson = N'17.5', UpdatedAt = GETUTCDATE();
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Ui.ThemeMode' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Ui.ThemeMode', N'Light', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Phase31Applied' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Phase31Applied', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Version' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN MATCHED THEN
    UPDATE SET ConfigJson = N'26', UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Version', N'26', GETUTCDATE());
GO

PRINT N'Phase 31 light theme / role workspace schema applied.';
GO
