/*
    Albert Retail Terminal — Phase 22 diagnostic telemetry
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\012_SystemDiagnostics.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF OBJECT_ID(N'dbo.DiagnosticTelemetryEvents', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DiagnosticTelemetryEvents
    (
        EventId         BIGINT          IDENTITY(1,1) NOT NULL,
        CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_DiagnosticTelemetryEvents_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        Category        VARCHAR(40)     NOT NULL,
        Severity        VARCHAR(20)     NOT NULL,
        Source          NVARCHAR(120)   NOT NULL,
        Message         NVARCHAR(500)   NOT NULL,
        DetailJson      NVARCHAR(MAX)   NULL,
        LatencyMs       INT             NULL,
        HttpStatus      NVARCHAR(40)    NULL,
        CONSTRAINT PK_DiagnosticTelemetryEvents PRIMARY KEY CLUSTERED (EventId)
    );

    CREATE INDEX IX_DiagnosticTelemetryEvents_Created
        ON dbo.DiagnosticTelemetryEvents (CreatedAtUtc DESC, EventId DESC);

    CREATE INDEX IX_DiagnosticTelemetryEvents_CategorySeverity
        ON dbo.DiagnosticTelemetryEvents (Category, Severity, CreatedAtUtc DESC);
END
GO
