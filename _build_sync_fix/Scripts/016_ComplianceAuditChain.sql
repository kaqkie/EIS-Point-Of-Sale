/*
    Phase 29 — tamper-evident fiscal compliance audit chain
    Run: sqlcmd -S .\SQLEXPRESS -E -d PointOfSale -i Scripts\016_ComplianceAuditChain.sql
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.ComplianceAuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ComplianceAuditLog
    (
        EntryId             BIGINT          IDENTITY(1,1) NOT NULL,
        CreatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_ComplianceAuditLog_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        Category            VARCHAR(40)     NOT NULL,
        Action              VARCHAR(80)     NOT NULL,
        OperatorUsername    NVARCHAR(80)    NOT NULL CONSTRAINT DF_ComplianceAuditLog_Operator DEFAULT (N'system'),
        CorrelationId       VARCHAR(100)    NULL,
        Detail              NVARCHAR(2000)  NOT NULL,
        Success             BIT             NOT NULL,
        PreviousHash        CHAR(64)        NOT NULL,
        EntryHash           CHAR(64)        NOT NULL,
        CONSTRAINT PK_ComplianceAuditLog PRIMARY KEY CLUSTERED (EntryId)
    );

    CREATE INDEX IX_ComplianceAuditLog_CreatedAtUtc
        ON dbo.ComplianceAuditLog (CreatedAtUtc DESC, EntryId DESC);

    CREATE INDEX IX_ComplianceAuditLog_Category
        ON dbo.ComplianceAuditLog (Category, CreatedAtUtc DESC);
END
GO

PRINT N'Phase 29 compliance audit chain applied.';
GO
