/*
    Phase 32 — terminal license activation table
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

PRINT N'Phase 32 MigrationScripts\011 applied.';
GO
