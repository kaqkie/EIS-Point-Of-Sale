/*
    Albert Retail Terminal — Phase 15 RBAC operators & security audit
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\007_OperatorSecurity.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF OBJECT_ID(N'dbo.Operators', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Operators
    (
        OperatorId          INT             IDENTITY(1,1) NOT NULL,
        Username            NVARCHAR(64)    NOT NULL,
        DisplayName         NVARCHAR(100)   NOT NULL,
        Role                VARCHAR(40)     NOT NULL,
        PasswordHash        NVARCHAR(200)   NOT NULL,
        PasswordSalt        NVARCHAR(200)   NOT NULL,
        PasswordIterations  INT             NOT NULL CONSTRAINT DF_Operators_Iterations DEFAULT (100000),
        IsActive            BIT             NOT NULL CONSTRAINT DF_Operators_IsActive DEFAULT (1),
        FailedLoginCount    INT             NOT NULL CONSTRAINT DF_Operators_Failed DEFAULT (0),
        LockoutUntilUtc     DATETIME2(7)    NULL,
        CreatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_Operators_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        LastLoginAtUtc      DATETIME2(7)    NULL,
        CONSTRAINT PK_Operators PRIMARY KEY CLUSTERED (OperatorId),
        CONSTRAINT UX_Operators_Username UNIQUE (Username)
    );
END
GO

IF OBJECT_ID(N'dbo.SecurityAuditLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SecurityAuditLog
    (
        AuditId         BIGINT          IDENTITY(1,1) NOT NULL,
        CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_SecurityAuditLog_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        OperatorId      INT             NULL,
        Username        NVARCHAR(64)    NOT NULL CONSTRAINT DF_SecurityAuditLog_Username DEFAULT (N''),
        Action          VARCHAR(80)     NOT NULL,
        Detail          NVARCHAR(2000)  NULL,
        Success         BIT             NOT NULL CONSTRAINT DF_SecurityAuditLog_Success DEFAULT (1),
        CONSTRAINT PK_SecurityAuditLog PRIMARY KEY CLUSTERED (AuditId)
    );

    CREATE INDEX IX_SecurityAuditLog_CreatedAtUtc
        ON dbo.SecurityAuditLog (CreatedAtUtc DESC, AuditId DESC);
END
GO
