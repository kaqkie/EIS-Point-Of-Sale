/*
    Albert Retail Terminal — Phase 12 analytics & shift management
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\004_AnalyticsAndShifts.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF OBJECT_ID(N'dbo.CashierShifts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CashierShifts
    (
        ShiftId             INT             IDENTITY(1,1) NOT NULL,
        OpenedAtUtc         DATETIME2(7)    NOT NULL CONSTRAINT DF_CashierShifts_OpenedAtUtc DEFAULT (SYSUTCDATETIME()),
        ClosedAtUtc         DATETIME2(7)    NULL,
        CashierName         NVARCHAR(100)   NOT NULL,
        OpeningFloat        DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_CashierShifts_OpeningFloat DEFAULT (0),
        ClosingCashCounted  DECIMAL(18, 2)  NULL,
        ExpectedCash        DECIMAL(18, 2)  NULL,
        CashVariance        DECIMAL(18, 2)  NULL,
        Status              VARCHAR(20)     NOT NULL
            CONSTRAINT CK_CashierShifts_Status CHECK (Status IN (N'Open', N'Closed')),
        ZReportJson         NVARCHAR(MAX)   NULL,
        Notes               NVARCHAR(500)   NULL,
        CONSTRAINT PK_CashierShifts PRIMARY KEY CLUSTERED (ShiftId)
    );

    CREATE INDEX IX_CashierShifts_Status_OpenedAt
        ON dbo.CashierShifts (Status, OpenedAtUtc DESC);
END
GO

IF OBJECT_ID(N'dbo.ShiftCashMovements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShiftCashMovements
    (
        MovementId      INT             IDENTITY(1,1) NOT NULL,
        ShiftId         INT             NOT NULL,
        MovementType    VARCHAR(20)     NOT NULL
            CONSTRAINT CK_ShiftCashMovements_Type CHECK (MovementType IN (N'CashIn', N'CashOut')),
        Amount          DECIMAL(18, 2)  NOT NULL,
        Reason          NVARCHAR(200)   NULL,
        CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_ShiftCashMovements_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ShiftCashMovements PRIMARY KEY CLUSTERED (MovementId),
        CONSTRAINT FK_ShiftCashMovements_Shift FOREIGN KEY (ShiftId) REFERENCES dbo.CashierShifts (ShiftId)
    );

    CREATE INDEX IX_ShiftCashMovements_ShiftId ON dbo.ShiftCashMovements (ShiftId, CreatedAtUtc);
END
GO

CREATE OR ALTER VIEW dbo.vw_SyncedInvoiceFacts
AS
SELECT
    q.Id AS QueueId,
    q.CreatedAt,
    q.Status,
    JSON_VALUE(q.PayloadJson, '$.invoiceHeader.invoiceNumber') AS InvoiceNumber,
    JSON_VALUE(q.PayloadJson, '$.invoiceHeader.paymentMethod') AS PaymentMethod,
    TRY_CAST(JSON_VALUE(q.PayloadJson, '$.invoiceSummary.invoiceTotal') AS DECIMAL(18, 2)) AS InvoiceTotal,
    TRY_CAST(JSON_VALUE(q.PayloadJson, '$.invoiceSummary.totalVAT') AS DECIMAL(18, 2)) AS TotalVat,
    TRY_CAST(JSON_VALUE(q.PayloadJson, '$.invoiceSummary.amountTendered') AS DECIMAL(18, 2)) AS AmountTendered,
    JSON_VALUE(q.FiscalResponseJson, '$.fiscalSignature') AS FiscalSignature,
    JSON_VALUE(q.FiscalResponseJson, '$.verificationUrl') AS VerificationUrl
FROM dbo.OfflineInvoiceQueue AS q
WHERE q.Status = N'SYNCED';
GO

CREATE OR ALTER VIEW dbo.vw_TaxBreakdownFacts
AS
SELECT
    q.Id AS QueueId,
    q.CreatedAt,
    JSON_VALUE(q.PayloadJson, '$.invoiceHeader.invoiceNumber') AS InvoiceNumber,
    UPPER(LTRIM(RTRIM(JSON_VALUE(tax.value, '$.rateId')))) AS TaxRateId,
    TRY_CAST(JSON_VALUE(tax.value, '$.taxableAmount') AS DECIMAL(18, 2)) AS TaxableAmount,
    TRY_CAST(JSON_VALUE(tax.value, '$.taxAmount') AS DECIMAL(18, 2)) AS TaxAmount
FROM dbo.OfflineInvoiceQueue AS q
CROSS APPLY OPENJSON(q.PayloadJson, '$.invoiceSummary.taxBreakDown') AS tax
WHERE q.Status = N'SYNCED';
GO

CREATE OR ALTER VIEW dbo.vw_TaxReconciliationDaily
AS
SELECT
    CAST(CreatedAt AS DATE) AS BusinessDate,
    TaxRateId,
    SUM(ISNULL(TaxableAmount, 0)) AS TaxableTotal,
    SUM(ISNULL(TaxAmount, 0)) AS VatCollected,
    COUNT(DISTINCT QueueId) AS InvoiceCount
FROM dbo.vw_TaxBreakdownFacts
GROUP BY CAST(CreatedAt AS DATE), TaxRateId;
GO

CREATE OR ALTER VIEW dbo.vw_QueueHealthHourly
AS
SELECT
    DATEADD(HOUR, DATEDIFF(HOUR, 0, CreatedAt), 0) AS HourBucketUtc,
    Status,
    COUNT(*) AS ItemCount
FROM dbo.OfflineInvoiceQueue
GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, CreatedAt), 0), Status;
GO

PRINT N'Phase 12 analytics and shift schema applied.';
GO
