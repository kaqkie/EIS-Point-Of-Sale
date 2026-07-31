/*
    Phase 34 — Supervisor PIN columns on Operators
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

PRINT N'Phase 34 MigrationScripts\013 applied.';
GO
