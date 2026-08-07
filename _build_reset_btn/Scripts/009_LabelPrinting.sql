/*
    Albert Retail Terminal — Phase 19 barcode / shelf-edge label batch tables
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\009_LabelPrinting.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF OBJECT_ID(N'dbo.LabelPrintBatches', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LabelPrintBatches
    (
        BatchId             BIGINT          IDENTITY(1,1) NOT NULL,
        TemplateType        NVARCHAR(40)    NOT NULL,
        QuantityPerItem     INT             NOT NULL CONSTRAINT DF_LabelPrintBatches_Qty DEFAULT (1),
        ProductCount        INT             NOT NULL CONSTRAINT DF_LabelPrintBatches_Products DEFAULT (0),
        LabelCount          INT             NOT NULL CONSTRAINT DF_LabelPrintBatches_Labels DEFAULT (0),
        Status              VARCHAR(20)     NOT NULL CONSTRAINT DF_LabelPrintBatches_Status DEFAULT ('Draft'),
        OperatorUsername    NVARCHAR(80)    NULL,
        Notes               NVARCHAR(400)   NULL,
        CreatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_LabelPrintBatches_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        PrintedAtUtc        DATETIME2(7)    NULL,
        CONSTRAINT PK_LabelPrintBatches PRIMARY KEY CLUSTERED (BatchId)
    );

    CREATE INDEX IX_LabelPrintBatches_Created
        ON dbo.LabelPrintBatches (CreatedAtUtc DESC, BatchId DESC);
END
GO

IF OBJECT_ID(N'dbo.LabelPrintBatchLines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LabelPrintBatchLines
    (
        BatchLineId     BIGINT          IDENTITY(1,1) NOT NULL,
        BatchId         BIGINT          NOT NULL,
        ProductCode     NVARCHAR(100)   NOT NULL,
        ProductName     NVARCHAR(200)   NOT NULL,
        UnitPriceNet    DECIMAL(18, 2)  NOT NULL,
        UnitPriceGross  DECIMAL(18, 2)  NOT NULL,
        Quantity        INT             NOT NULL CONSTRAINT DF_LabelPrintBatchLines_Qty DEFAULT (1),
        Symbology       VARCHAR(20)     NOT NULL CONSTRAINT DF_LabelPrintBatchLines_Sym DEFAULT ('Code128'),
        CONSTRAINT PK_LabelPrintBatchLines PRIMARY KEY CLUSTERED (BatchLineId),
        CONSTRAINT FK_LabelPrintBatchLines_Batch FOREIGN KEY (BatchId)
            REFERENCES dbo.LabelPrintBatches (BatchId)
    );

    CREATE INDEX IX_LabelPrintBatchLines_Batch
        ON dbo.LabelPrintBatchLines (BatchId, BatchLineId);
END
GO
