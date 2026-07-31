/*
    Albert Retail Terminal — Phase 23 financial EOD / Z-Report closures
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\013_FinancialClosure.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF OBJECT_ID(N'dbo.FinancialClosures', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FinancialClosures
    (
        ClosureId                     BIGINT          IDENTITY(1,1) NOT NULL,
        BusinessDate                  DATE            NOT NULL,
        ClosedAtUtc                   DATETIME2(7)    NOT NULL CONSTRAINT DF_FinancialClosures_ClosedAtUtc DEFAULT (SYSUTCDATETIME()),
        ClosedByUsername              NVARCHAR(80)    NOT NULL,
        ClosedByDisplayName           NVARCHAR(120)   NOT NULL,
        TotalGrossSalesMwk            DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_Gross DEFAULT (0),
        TotalTaxableSalesMwk          DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_Taxable DEFAULT (0),
        TotalVatCollectedMwk          DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_Vat DEFAULT (0),
        ExpectedVatMwk                DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_ExpVat DEFAULT (0),
        VatVarianceMwk                DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_VatVar DEFAULT (0),
        CashCollectionsMwk            DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_Cash DEFAULT (0),
        CardSettlementsMwk            DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_Card DEFAULT (0),
        MobileMoneySettlementsMwk     DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_Mobile DEFAULT (0),
        OtherSettlementsMwk           DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_Other DEFAULT (0),
        TotalVoidsMwk                 DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_Voids DEFAULT (0),
        VoidCount                     INT             NOT NULL CONSTRAINT DF_FinancialClosures_VoidCount DEFAULT (0),
        SyncedInvoiceCount            INT             NOT NULL CONSTRAINT DF_FinancialClosures_Synced DEFAULT (0),
        PendingInvoiceCount           INT             NOT NULL CONSTRAINT DF_FinancialClosures_Pending DEFAULT (0),
        QuarantinedInvoiceCount       INT             NOT NULL CONSTRAINT DF_FinancialClosures_Quarantined DEFAULT (0),
        FiscalSignatureMatchCount     INT             NOT NULL CONSTRAINT DF_FinancialClosures_SigMatch DEFAULT (0),
        FiscalSignatureMissingCount   INT             NOT NULL CONSTRAINT DF_FinancialClosures_SigMissing DEFAULT (0),
        CashDrawerVarianceMwk         DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_CashVar DEFAULT (0),
        CumulativeGrossSalesMwk       DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_CumGross DEFAULT (0),
        CumulativeVatMwk              DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FinancialClosures_CumVat DEFAULT (0),
        ShiftCount                    INT             NOT NULL CONSTRAINT DF_FinancialClosures_Shifts DEFAULT (0),
        AuditPassed                   BIT             NOT NULL CONSTRAINT DF_FinancialClosures_Audit DEFAULT (0),
        Status                        VARCHAR(20)     NOT NULL CONSTRAINT DF_FinancialClosures_Status DEFAULT ('Closed'),
        Notes                         NVARCHAR(1000)  NULL,
        ClosureJson                   NVARCHAR(MAX)   NULL,
        CONSTRAINT PK_FinancialClosures PRIMARY KEY CLUSTERED (ClosureId)
    );

    CREATE UNIQUE INDEX UX_FinancialClosures_BusinessDate_Closed
        ON dbo.FinancialClosures (BusinessDate)
        WHERE Status = 'Closed';

    CREATE INDEX IX_FinancialClosures_ClosedAt
        ON dbo.FinancialClosures (ClosedAtUtc DESC, ClosureId DESC);
END
GO
