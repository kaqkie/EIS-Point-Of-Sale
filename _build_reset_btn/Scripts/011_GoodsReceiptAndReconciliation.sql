/*
    Albert Retail Terminal — Phase 21 GRN & supplier invoice reconciliation
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\011_GoodsReceiptAndReconciliation.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF COL_LENGTH(N'dbo.LocalInventory', N'AverageUnitCost') IS NULL
    ALTER TABLE dbo.LocalInventory ADD AverageUnitCost DECIMAL(18, 2) NOT NULL
        CONSTRAINT DF_LocalInventory_AverageUnitCost DEFAULT (0);
GO

IF COL_LENGTH(N'dbo.LocalInventory', N'MarkupPercent') IS NULL
    ALTER TABLE dbo.LocalInventory ADD MarkupPercent DECIMAL(9, 4) NOT NULL
        CONSTRAINT DF_LocalInventory_MarkupPercent DEFAULT (0);
GO

IF OBJECT_ID(N'dbo.GoodsReceiptNotes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.GoodsReceiptNotes
    (
        GrnId                   BIGINT          IDENTITY(1,1) NOT NULL,
        GrnNumber               NVARCHAR(80)    NOT NULL,
        PoId                    BIGINT          NOT NULL,
        PoNumber                NVARCHAR(80)    NOT NULL,
        SupplierCode            NVARCHAR(40)    NOT NULL,
        SupplierName            NVARCHAR(150)   NOT NULL,
        Status                  VARCHAR(20)     NOT NULL CONSTRAINT DF_GoodsReceiptNotes_Status DEFAULT ('Draft'),
        DeliveryNoteNumber      NVARCHAR(80)    NULL,
        SupplierInvoiceNumber   NVARCHAR(80)    NULL,
        OperatorUsername        NVARCHAR(80)    NULL,
        Notes                   NVARCHAR(400)   NULL,
        CreatedAtUtc            DATETIME2(7)    NOT NULL CONSTRAINT DF_GoodsReceiptNotes_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        PostedAtUtc             DATETIME2(7)    NULL,
        CONSTRAINT PK_GoodsReceiptNotes PRIMARY KEY CLUSTERED (GrnId),
        CONSTRAINT UX_GoodsReceiptNotes_GrnNumber UNIQUE (GrnNumber)
    );

    CREATE INDEX IX_GoodsReceiptNotes_Po ON dbo.GoodsReceiptNotes (PoId, CreatedAtUtc DESC);
END
GO

IF OBJECT_ID(N'dbo.GoodsReceiptLines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.GoodsReceiptLines
    (
        GrnLineId           BIGINT          IDENTITY(1,1) NOT NULL,
        GrnId               BIGINT          NOT NULL,
        ProductCode         NVARCHAR(100)   NOT NULL,
        ProductName         NVARCHAR(200)   NOT NULL,
        OrderedQty          DECIMAL(18, 2)  NOT NULL,
        ReceivedQty         DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_GoodsReceiptLines_Received DEFAULT (0),
        DamagedQty          DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_GoodsReceiptLines_Damaged DEFAULT (0),
        UnitCost            DECIMAL(18, 2)  NOT NULL,
        PreviousStock       DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_GoodsReceiptLines_PrevStock DEFAULT (0),
        NewStock            DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_GoodsReceiptLines_NewStock DEFAULT (0),
        PreviousAvgCost     DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_GoodsReceiptLines_PrevAvg DEFAULT (0),
        NewAvgCost          DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_GoodsReceiptLines_NewAvg DEFAULT (0),
        PreviousRetailPrice DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_GoodsReceiptLines_PrevRetail DEFAULT (0),
        NewRetailPrice      DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_GoodsReceiptLines_NewRetail DEFAULT (0),
        LineNotes           NVARCHAR(300)   NULL,
        CONSTRAINT PK_GoodsReceiptLines PRIMARY KEY CLUSTERED (GrnLineId),
        CONSTRAINT FK_GoodsReceiptLines_Grn FOREIGN KEY (GrnId) REFERENCES dbo.GoodsReceiptNotes (GrnId)
    );

    CREATE INDEX IX_GoodsReceiptLines_Grn ON dbo.GoodsReceiptLines (GrnId, GrnLineId);
END
GO

IF OBJECT_ID(N'dbo.SupplierInvoiceReconciliations', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupplierInvoiceReconciliations
    (
        ReconciliationId        BIGINT          IDENTITY(1,1) NOT NULL,
        GrnId                   BIGINT          NOT NULL,
        GrnNumber               NVARCHAR(80)    NOT NULL,
        SupplierInvoiceNumber   NVARCHAR(80)    NOT NULL,
        InvoiceDate             DATE            NULL,
        InvoiceTotalMwk         DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_SupplierInvoiceReconciliations_Inv DEFAULT (0),
        ReceivedTotalMwk        DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_SupplierInvoiceReconciliations_Recv DEFAULT (0),
        VarianceMwk             DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_SupplierInvoiceReconciliations_Var DEFAULT (0),
        Status                  VARCHAR(30)     NOT NULL CONSTRAINT DF_SupplierInvoiceReconciliations_Status DEFAULT ('Pending'),
        DiscrepancyNotes        NVARCHAR(1000)  NULL,
        OperatorUsername        NVARCHAR(80)    NULL,
        CreatedAtUtc            DATETIME2(7)    NOT NULL CONSTRAINT DF_SupplierInvoiceReconciliations_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        SignedOffAtUtc          DATETIME2(7)    NULL,
        CONSTRAINT PK_SupplierInvoiceReconciliations PRIMARY KEY CLUSTERED (ReconciliationId)
    );

    CREATE INDEX IX_SupplierInvoiceReconciliations_Created
        ON dbo.SupplierInvoiceReconciliations (CreatedAtUtc DESC, ReconciliationId DESC);
END
GO

IF OBJECT_ID(N'dbo.SupplierInvoiceReconciliationLines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupplierInvoiceReconciliationLines
    (
        ReconciliationLineId    BIGINT          IDENTITY(1,1) NOT NULL,
        ReconciliationId        BIGINT          NOT NULL,
        ProductCode             NVARCHAR(100)   NOT NULL,
        ProductName             NVARCHAR(200)   NOT NULL,
        DiscrepancyType         VARCHAR(40)     NOT NULL,
        OrderedQty              DECIMAL(18, 2)  NOT NULL,
        ReceivedQty             DECIMAL(18, 2)  NOT NULL,
        DamagedQty              DECIMAL(18, 2)  NOT NULL,
        InvoiceQty              DECIMAL(18, 2)  NOT NULL,
        UnitCost                DECIMAL(18, 2)  NOT NULL,
        InvoiceUnitCost         DECIMAL(18, 2)  NOT NULL,
        Message                 NVARCHAR(400)   NOT NULL,
        CONSTRAINT PK_SupplierInvoiceReconciliationLines PRIMARY KEY CLUSTERED (ReconciliationLineId),
        CONSTRAINT FK_SupplierInvoiceReconciliationLines_Header FOREIGN KEY (ReconciliationId)
            REFERENCES dbo.SupplierInvoiceReconciliations (ReconciliationId)
    );

    CREATE INDEX IX_SupplierInvoiceReconciliationLines_Header
        ON dbo.SupplierInvoiceReconciliationLines (ReconciliationId, ReconciliationLineId);
END
GO
