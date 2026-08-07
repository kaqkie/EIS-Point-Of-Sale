/*
    Phase 31 — light UI workspace preferences + statutory VAT config key
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

PRINT N'Phase 31 MigrationScripts\010 applied.';
GO
