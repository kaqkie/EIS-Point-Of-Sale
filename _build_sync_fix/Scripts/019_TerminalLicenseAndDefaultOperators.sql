/*
    Phase 32 — terminal license activation table + seed config flags
    Run: sqlcmd -S .\SQLEXPRESS -E -d PointOfSale -i Scripts\019_TerminalLicenseAndDefaultOperators.sql
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.TerminalLicenseActivation', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TerminalLicenseActivation
    (
        ActivationId        INT             IDENTITY(1,1) NOT NULL,
        LicenseKeyHash      CHAR(64)        NOT NULL,
        MaskedLicenseKey    VARCHAR(32)     NOT NULL,
        ActivatedAtUtc      DATETIME2(7)    NOT NULL CONSTRAINT DF_TerminalLicenseActivation_ActivatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ActivatedByHost     NVARCHAR(128)   NULL,
        IsActive            BIT             NOT NULL CONSTRAINT DF_TerminalLicenseActivation_IsActive DEFAULT (1),
        CONSTRAINT PK_TerminalLicenseActivation PRIMARY KEY CLUSTERED (ActivationId)
    );

    CREATE UNIQUE INDEX UX_TerminalLicenseActivation_Hash
        ON dbo.TerminalLicenseActivation (LicenseKeyHash)
        WHERE IsActive = 1;
END
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Terminal.License.RequireActivation' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Terminal.License.RequireActivation', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Phase32Applied' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Phase32Applied', N'true', GETUTCDATE());
GO

MERGE dbo.Configurations AS target
USING (SELECT N'Schema.Version' AS ConfigKey) AS source
ON target.ConfigKey = source.ConfigKey
WHEN MATCHED THEN
    UPDATE SET ConfigJson = N'27', UpdatedAt = GETUTCDATE()
WHEN NOT MATCHED THEN
    INSERT (ConfigKey, ConfigJson, UpdatedAt)
    VALUES (N'Schema.Version', N'27', GETUTCDATE());
GO

PRINT N'Phase 32 terminal license schema applied. Operator seeds are applied by InitialDataSeeder at app startup.';
GO
