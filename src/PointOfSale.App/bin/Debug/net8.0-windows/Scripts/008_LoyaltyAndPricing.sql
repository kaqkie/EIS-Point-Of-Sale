/*
    Albert Retail Terminal — Phase 16 loyalty & promotional pricing
    Run: sqlcmd -S .\SQLEXPRESS -E -i Scripts\008_LoyaltyAndPricing.sql
*/
SET NOCOUNT ON;
USE PointOfSale;
GO

IF OBJECT_ID(N'dbo.LoyaltyMembers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoyaltyMembers
    (
        MemberId            INT             IDENTITY(1,1) NOT NULL,
        MemberCode          NVARCHAR(40)    NOT NULL,
        FullName            NVARCHAR(150)   NOT NULL,
        Phone               NVARCHAR(40)    NULL,
        PointsBalance       DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_LoyaltyMembers_Points DEFAULT (0),
        LifetimeSpendMwk    DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_LoyaltyMembers_Spend DEFAULT (0),
        IsActive            BIT             NOT NULL CONSTRAINT DF_LoyaltyMembers_IsActive DEFAULT (1),
        CreatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_LoyaltyMembers_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        LastPurchaseAtUtc   DATETIME2(7)    NULL,
        CONSTRAINT PK_LoyaltyMembers PRIMARY KEY CLUSTERED (MemberId),
        CONSTRAINT UX_LoyaltyMembers_MemberCode UNIQUE (MemberCode)
    );
END
GO

IF OBJECT_ID(N'dbo.LoyaltyLedger', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.LoyaltyLedger
    (
        LedgerId        BIGINT          IDENTITY(1,1) NOT NULL,
        MemberId        INT             NOT NULL,
        EntryType       VARCHAR(20)     NOT NULL,
        Points          DECIMAL(18, 2)  NOT NULL,
        AmountMwk       DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_LoyaltyLedger_Amount DEFAULT (0),
        InvoiceNumber   NVARCHAR(80)    NULL,
        Notes           NVARCHAR(300)   NULL,
        CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_LoyaltyLedger_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_LoyaltyLedger PRIMARY KEY CLUSTERED (LedgerId),
        CONSTRAINT FK_LoyaltyLedger_Member FOREIGN KEY (MemberId) REFERENCES dbo.LoyaltyMembers (MemberId)
    );

    CREATE INDEX IX_LoyaltyLedger_Member_Created
        ON dbo.LoyaltyLedger (MemberId, CreatedAtUtc DESC);
END
GO

IF OBJECT_ID(N'dbo.PricingRules', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PricingRules
    (
        RuleId          INT             IDENTITY(1,1) NOT NULL,
        Name            NVARCHAR(120)   NOT NULL,
        RuleType        VARCHAR(40)     NOT NULL,
        CategoryCode    NVARCHAR(40)    NULL,
        ProductCode     NVARCHAR(100)   NULL,
        PercentOff      DECIMAL(9, 4)   NOT NULL CONSTRAINT DF_PricingRules_Percent DEFAULT (0),
        BuyQuantity     DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_PricingRules_Buy DEFAULT (1),
        FreeQuantity    DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_PricingRules_Free DEFAULT (1),
        PromoUnitPrice  DECIMAL(18, 2)  NULL,
        StartsAtUtc     DATETIME2(7)    NOT NULL,
        EndsAtUtc       DATETIME2(7)    NULL,
        Priority        INT             NOT NULL CONSTRAINT DF_PricingRules_Priority DEFAULT (0),
        IsActive        BIT             NOT NULL CONSTRAINT DF_PricingRules_IsActive DEFAULT (1),
        CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_PricingRules_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_PricingRules PRIMARY KEY CLUSTERED (RuleId)
    );

    CREATE INDEX IX_PricingRules_Active_Priority
        ON dbo.PricingRules (IsActive, Priority DESC, StartsAtUtc);
END
GO
