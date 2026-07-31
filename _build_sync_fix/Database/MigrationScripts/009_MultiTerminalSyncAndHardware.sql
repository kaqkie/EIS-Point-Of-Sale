/*
    Phase 30 — multi-terminal sync ledger, heartbeats, peripheral diagnostics
    (Database/MigrationScripts mirror of Scripts\017_MultiTerminalSyncAndHardware.sql)
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF OBJECT_ID(N'dbo.Seq_MultiTerminalSync', N'SO') IS NULL
BEGIN
    CREATE SEQUENCE dbo.Seq_MultiTerminalSync AS BIGINT START WITH 1 INCREMENT BY 1;
END
GO

IF OBJECT_ID(N'dbo.TerminalHeartbeat', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TerminalHeartbeat
    (
        HeartbeatId             BIGINT          IDENTITY(1,1) NOT NULL,
        TerminalId              NVARCHAR(80)    NOT NULL,
        BranchId                NVARCHAR(80)    NOT NULL,
        LastSeenUtc             DATETIME2(7)    NOT NULL CONSTRAINT DF_TerminalHeartbeat_LastSeenUtc DEFAULT (SYSUTCDATETIME()),
        Status                  VARCHAR(20)     NOT NULL CONSTRAINT DF_TerminalHeartbeat_Status DEFAULT (N'Online'),
        HostName                NVARCHAR(128)   NULL,
        PendingOfflineInvoices  INT             NOT NULL CONSTRAINT DF_TerminalHeartbeat_PendingOffline DEFAULT (0),
        OpenShiftExpectedCash   DECIMAL(18,2)   NOT NULL CONSTRAINT DF_TerminalHeartbeat_ExpectedCash DEFAULT (0),
        OpenShiftCashier        NVARCHAR(120)   NULL,
        CONSTRAINT PK_TerminalHeartbeat PRIMARY KEY CLUSTERED (HeartbeatId),
        CONSTRAINT UQ_TerminalHeartbeat_TerminalId UNIQUE (TerminalId)
    );

    CREATE INDEX IX_TerminalHeartbeat_Branch_LastSeen
        ON dbo.TerminalHeartbeat (BranchId, LastSeenUtc DESC);
END
GO

IF OBJECT_ID(N'dbo.MultiTerminalSyncLedger', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MultiTerminalSyncLedger
    (
        LedgerId                BIGINT          IDENTITY(1,1) NOT NULL,
        BranchId                NVARCHAR(80)    NOT NULL,
        SourceTerminalId        NVARCHAR(80)    NOT NULL,
        EventType               VARCHAR(40)     NOT NULL,
        EntityKey               NVARCHAR(120)   NOT NULL,
        PayloadJson             NVARCHAR(MAX)   NOT NULL,
        SequenceNumber          BIGINT          NOT NULL,
        CreatedAtUtc            DATETIME2(7)    NOT NULL CONSTRAINT DF_MultiTerminalSyncLedger_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        AppliedAtUtc            DATETIME2(7)    NULL,
        AppliedByTerminalId     NVARCHAR(80)    NULL,
        CONSTRAINT PK_MultiTerminalSyncLedger PRIMARY KEY CLUSTERED (LedgerId)
    );

    CREATE UNIQUE INDEX UX_MultiTerminalSyncLedger_Sequence
        ON dbo.MultiTerminalSyncLedger (BranchId, SequenceNumber);

    CREATE INDEX IX_MultiTerminalSyncLedger_Pending
        ON dbo.MultiTerminalSyncLedger (BranchId, SequenceNumber)
        INCLUDE (SourceTerminalId, EventType);
END
GO

IF OBJECT_ID(N'dbo.MultiTerminalSyncCursor', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.MultiTerminalSyncCursor
    (
        BranchId                NVARCHAR(80)    NOT NULL,
        TerminalId              NVARCHAR(80)    NOT NULL,
        LastAppliedSequence     BIGINT          NOT NULL CONSTRAINT DF_MultiTerminalSyncCursor_Seq DEFAULT (0),
        UpdatedAtUtc            DATETIME2(7)    NOT NULL CONSTRAINT DF_MultiTerminalSyncCursor_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_MultiTerminalSyncCursor PRIMARY KEY CLUSTERED (BranchId, TerminalId)
    );
END
GO

IF OBJECT_ID(N'dbo.PeripheralDiagnosticLog', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PeripheralDiagnosticLog
    (
        LogId                   BIGINT          IDENTITY(1,1) NOT NULL,
        CreatedAtUtc            DATETIME2(7)    NOT NULL CONSTRAINT DF_PeripheralDiagnosticLog_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        TerminalId              NVARCHAR(80)    NOT NULL,
        PeripheralType          VARCHAR(40)     NOT NULL,
        Action                  VARCHAR(80)     NOT NULL,
        Success                 BIT             NOT NULL,
        Detail                  NVARCHAR(2000)  NULL,
        CONSTRAINT PK_PeripheralDiagnosticLog PRIMARY KEY CLUSTERED (LogId)
    );

    CREATE INDEX IX_PeripheralDiagnosticLog_CreatedAtUtc
        ON dbo.PeripheralDiagnosticLog (CreatedAtUtc DESC, LogId DESC);
END
GO

PRINT N'Phase 30 MigrationScripts\009 applied.';
GO
