/*
    Albert Retail Terminal — Phase 20 stock alerts & purchase orders
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\010_InventoryAlertsAndPurchaseOrders.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF COL_LENGTH(N'dbo.LocalInventory', N'MinReorderQty') IS NULL
    ALTER TABLE dbo.LocalInventory ADD MinReorderQty DECIMAL(18, 2) NOT NULL
        CONSTRAINT DF_LocalInventory_MinReorderQty DEFAULT (0);
GO

IF COL_LENGTH(N'dbo.LocalInventory', N'MaxStockCapacity') IS NULL
    ALTER TABLE dbo.LocalInventory ADD MaxStockCapacity DECIMAL(18, 2) NOT NULL
        CONSTRAINT DF_LocalInventory_MaxStockCapacity DEFAULT (0);
GO

IF COL_LENGTH(N'dbo.LocalInventory', N'SupplierCode') IS NULL
    ALTER TABLE dbo.LocalInventory ADD SupplierCode NVARCHAR(40) NULL;
GO

IF COL_LENGTH(N'dbo.LocalInventory', N'SupplierName') IS NULL
    ALTER TABLE dbo.LocalInventory ADD SupplierName NVARCHAR(150) NULL;
GO

IF OBJECT_ID(N'dbo.InventorySuppliers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventorySuppliers
    (
        SupplierCode    NVARCHAR(40)    NOT NULL,
        SupplierName    NVARCHAR(150)   NOT NULL,
        ContactEmail    NVARCHAR(120)   NULL,
        Phone           NVARCHAR(40)    NULL,
        Notes           NVARCHAR(400)   NULL,
        IsActive        BIT             NOT NULL CONSTRAINT DF_InventorySuppliers_IsActive DEFAULT (1),
        CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_InventorySuppliers_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_InventorySuppliers PRIMARY KEY CLUSTERED (SupplierCode)
    );
END
GO

IF OBJECT_ID(N'dbo.InventoryStockAlerts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventoryStockAlerts
    (
        AlertId             BIGINT          IDENTITY(1,1) NOT NULL,
        ProductCode         NVARCHAR(100)   NOT NULL,
        ProductName         NVARCHAR(200)   NOT NULL,
        AlertType           VARCHAR(30)     NOT NULL,
        Severity            VARCHAR(20)     NOT NULL,
        StockQuantity       DECIMAL(18, 2)  NOT NULL,
        ThresholdQty        DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_InventoryStockAlerts_Threshold DEFAULT (0),
        AverageDailySales   DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_InventoryStockAlerts_AvgDaily DEFAULT (0),
        SupplierCode        NVARCHAR(40)    NULL,
        Message             NVARCHAR(400)   NOT NULL,
        IsAcknowledged      BIT             NOT NULL CONSTRAINT DF_InventoryStockAlerts_Ack DEFAULT (0),
        ShiftId             INT             NULL,
        CreatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_InventoryStockAlerts_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        AcknowledgedAtUtc   DATETIME2(7)    NULL,
        CONSTRAINT PK_InventoryStockAlerts PRIMARY KEY CLUSTERED (AlertId)
    );

    CREATE INDEX IX_InventoryStockAlerts_Open
        ON dbo.InventoryStockAlerts (IsAcknowledged, Severity, CreatedAtUtc DESC);

    CREATE INDEX IX_InventoryStockAlerts_ProductType
        ON dbo.InventoryStockAlerts (ProductCode, AlertType, IsAcknowledged);
END
GO

IF OBJECT_ID(N'dbo.PurchaseOrders', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PurchaseOrders
    (
        PoId                BIGINT          IDENTITY(1,1) NOT NULL,
        PoNumber            NVARCHAR(80)    NOT NULL,
        SupplierCode        NVARCHAR(40)    NOT NULL,
        SupplierName        NVARCHAR(150)   NOT NULL,
        Status              VARCHAR(30)     NOT NULL CONSTRAINT DF_PurchaseOrders_Status DEFAULT ('Draft'),
        LineCount           INT             NOT NULL CONSTRAINT DF_PurchaseOrders_Lines DEFAULT (0),
        TotalQuantity       DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_PurchaseOrders_Qty DEFAULT (0),
        TotalEstimatedCost  DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_PurchaseOrders_Cost DEFAULT (0),
        OperatorUsername    NVARCHAR(80)    NULL,
        Notes               NVARCHAR(400)   NULL,
        SummaryText         NVARCHAR(1000)  NULL,
        GeneratedAtUtc      DATETIME2(7)    NOT NULL CONSTRAINT DF_PurchaseOrders_GeneratedAtUtc DEFAULT (SYSUTCDATETIME()),
        ExportedAtUtc       DATETIME2(7)    NULL,
        CONSTRAINT PK_PurchaseOrders PRIMARY KEY CLUSTERED (PoId),
        CONSTRAINT UX_PurchaseOrders_PoNumber UNIQUE (PoNumber)
    );

    CREATE INDEX IX_PurchaseOrders_Generated
        ON dbo.PurchaseOrders (GeneratedAtUtc DESC, PoId DESC);
END
GO

IF OBJECT_ID(N'dbo.PurchaseOrderLines', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PurchaseOrderLines
    (
        PoLineId            BIGINT          IDENTITY(1,1) NOT NULL,
        PoId                BIGINT          NOT NULL,
        ProductCode         NVARCHAR(100)   NOT NULL,
        ProductName         NVARCHAR(200)   NOT NULL,
        CurrentStock        DECIMAL(18, 2)  NOT NULL,
        MinReorderQty       DECIMAL(18, 2)  NOT NULL,
        MaxStockCapacity    DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_PurchaseOrderLines_MaxCap DEFAULT (0),
        AverageDailySales   DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_PurchaseOrderLines_Avg DEFAULT (0),
        SuggestedQty        DECIMAL(18, 2)  NOT NULL,
        UnitCost            DECIMAL(18, 2)  NOT NULL,
        LineTotal           DECIMAL(18, 2)  NOT NULL,
        CONSTRAINT PK_PurchaseOrderLines PRIMARY KEY CLUSTERED (PoLineId),
        CONSTRAINT FK_PurchaseOrderLines_Po FOREIGN KEY (PoId) REFERENCES dbo.PurchaseOrders (PoId)
    );

    CREATE INDEX IX_PurchaseOrderLines_Po
        ON dbo.PurchaseOrderLines (PoId, PoLineId);
END
GO
