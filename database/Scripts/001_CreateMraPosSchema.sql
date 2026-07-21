/*
    Point of Sale — MRA EIS local store (SQL Server Express)
    Phase 1: Terminals, Configurations, OfflineInvoiceQueue, LocalInventory
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'pos')
    EXEC(N'CREATE SCHEMA pos');
GO

/* -------------------------------------------------------------------------
   Terminals — activation identity, credentials, lifecycle
   ------------------------------------------------------------------------- */
IF OBJECT_ID(N'pos.Terminals', N'U') IS NULL
BEGIN
    CREATE TABLE pos.Terminals
    (
        TerminalId              NVARCHAR(50)    NOT NULL,
        TerminalActivationCode  NVARCHAR(50)    NULL,  -- retained for confirmation retry; protect at app layer
        ActivationDateUtc       DATETIME2(7)    NULL,
        ActivationStatus        TINYINT         NOT NULL
            CONSTRAINT CK_Terminals_ActivationStatus
            CHECK (ActivationStatus IN (0, 1, 2, 3)), -- 0=NotActivated, 1=PendingConfirmation, 2=Activated, 3=Deactivated
        JwtToken                NVARCHAR(MAX)   NULL,
        SecretKey               NVARCHAR(256)   NULL,
        ApiKey                  NVARCHAR(256)   NULL,
        Tin                     NVARCHAR(20)    NULL,
        GlobalConfigVersion     INT             NOT NULL CONSTRAINT DF_Terminals_GlobalConfigVersion DEFAULT (0),
        TerminalConfigVersion   INT             NOT NULL CONSTRAINT DF_Terminals_TerminalConfigVersion DEFAULT (0),
        TaxpayerConfigVersion   INT             NOT NULL CONSTRAINT DF_Terminals_TaxpayerConfigVersion DEFAULT (0),
        ProductId               NVARCHAR(50)    NULL,
        ProductVersion          NVARCHAR(50)    NULL,
        PlatformOsName          NVARCHAR(50)    NULL,
        PlatformOsVersion       NVARCHAR(50)    NULL,
        PlatformOsBuild         NVARCHAR(50)    NULL,
        PlatformMacAddress      NVARCHAR(17)    NULL,
        ApiEnvironment          NVARCHAR(20)    NOT NULL CONSTRAINT DF_Terminals_ApiEnvironment DEFAULT (N'Dev'),
        CreatedAtUtc            DATETIME2(7)    NOT NULL CONSTRAINT DF_Terminals_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedAtUtc            DATETIME2(7)    NOT NULL CONSTRAINT DF_Terminals_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Terminals PRIMARY KEY CLUSTERED (TerminalId)
    );

    CREATE UNIQUE INDEX UX_Terminals_MacAddress_Active
        ON pos.Terminals (PlatformMacAddress)
        WHERE ActivationStatus IN (1, 2) AND PlatformMacAddress IS NOT NULL;
END
GO

/* -------------------------------------------------------------------------
   Configurations — versioned MRA global / terminal / taxpayer payloads
   ------------------------------------------------------------------------- */
IF OBJECT_ID(N'pos.Configurations', N'U') IS NULL
BEGIN
    CREATE TABLE pos.Configurations
    (
        ConfigurationId     BIGINT          IDENTITY(1,1) NOT NULL,
        TerminalId          NVARCHAR(50)    NOT NULL,
        ConfigScope         TINYINT         NOT NULL
            CONSTRAINT CK_Configurations_ConfigScope
            CHECK (ConfigScope IN (1, 2, 3)), -- 1=Global, 2=Terminal, 3=Taxpayer
        VersionNo           INT             NOT NULL,
        PayloadJson         NVARCHAR(MAX)   NOT NULL,
        Source              NVARCHAR(30)    NOT NULL
            CONSTRAINT CK_Configurations_Source
            CHECK (Source IN (N'Activation', N'GetLatestConfigs', N'Manual')),
        IsCurrent           BIT             NOT NULL CONSTRAINT DF_Configurations_IsCurrent DEFAULT (0),
        RetrievedAtUtc      DATETIME2(7)    NOT NULL CONSTRAINT DF_Configurations_RetrievedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Configurations PRIMARY KEY CLUSTERED (ConfigurationId),
        CONSTRAINT FK_Configurations_Terminals
            FOREIGN KEY (TerminalId) REFERENCES pos.Terminals (TerminalId),
        CONSTRAINT UQ_Configurations_Terminal_Scope_Version
            UNIQUE (TerminalId, ConfigScope, VersionNo)
    );

    CREATE INDEX IX_Configurations_Terminal_Current
        ON pos.Configurations (TerminalId, ConfigScope, IsCurrent)
        INCLUDE (VersionNo, RetrievedAtUtc)
        WHERE IsCurrent = 1;
END
GO

/* -------------------------------------------------------------------------
   Offline invoice queue — strict FIFO + quarantine
   ------------------------------------------------------------------------- */
IF OBJECT_ID(N'pos.OfflineInvoiceQueue', N'U') IS NULL
BEGIN
    CREATE TABLE pos.OfflineInvoiceQueue
    (
        QueueId                 BIGINT          IDENTITY(1,1) NOT NULL,
        TerminalId              NVARCHAR(50)    NOT NULL,
        FifoSequence            BIGINT          NOT NULL,
        InvoiceNumber           NVARCHAR(100)   NOT NULL,
        InvoiceDateTimeUtc      DATETIME2(7)    NOT NULL,
        PayloadJson             NVARCHAR(MAX)   NOT NULL,
        OfflineSignature        NVARCHAR(512)   NULL,
        QueueStatus             TINYINT         NOT NULL
            CONSTRAINT CK_OfflineInvoiceQueue_QueueStatus
            CHECK (QueueStatus IN (0, 1, 2, 3, 4)), -- 0=Pending, 1=InProgress, 2=Submitted, 3=Failed, 4=Quarantined
        RetryCount              INT             NOT NULL CONSTRAINT DF_OfflineInvoiceQueue_RetryCount DEFAULT (0),
        LastError               NVARCHAR(MAX)   NULL,
        IsQuarantined           BIT             NOT NULL CONSTRAINT DF_OfflineInvoiceQueue_IsQuarantined DEFAULT (0),
        QuarantineReason        NVARCHAR(500)   NULL,
        QuarantinedAtUtc        DATETIME2(7)    NULL,
        QuarantineReleasedAtUtc DATETIME2(7)    NULL,
        QuarantineReleasedBy    NVARCHAR(100)   NULL,
        CreatedAtUtc            DATETIME2(7)    NOT NULL CONSTRAINT DF_OfflineInvoiceQueue_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        LockedAtUtc             DATETIME2(7)    NULL,
        SubmittedAtUtc          DATETIME2(7)    NULL,
        CONSTRAINT PK_OfflineInvoiceQueue PRIMARY KEY CLUSTERED (QueueId),
        CONSTRAINT FK_OfflineInvoiceQueue_Terminals
            FOREIGN KEY (TerminalId) REFERENCES pos.Terminals (TerminalId),
        CONSTRAINT UQ_OfflineInvoiceQueue_Terminal_FifoSequence
            UNIQUE (TerminalId, FifoSequence),
        CONSTRAINT UQ_OfflineInvoiceQueue_Terminal_InvoiceNumber
            UNIQUE (TerminalId, InvoiceNumber)
    );

    CREATE INDEX IX_OfflineInvoiceQueue_Fifo_Pending
        ON pos.OfflineInvoiceQueue (TerminalId, FifoSequence)
        INCLUDE (QueueId, InvoiceNumber, QueueStatus, IsQuarantined)
        WHERE QueueStatus = 0 AND IsQuarantined = 0;

    CREATE INDEX IX_OfflineInvoiceQueue_Quarantine
        ON pos.OfflineInvoiceQueue (TerminalId, QuarantinedAtUtc DESC)
        WHERE IsQuarantined = 1;
END
GO

/* FIFO sequence allocator (per terminal) */
IF OBJECT_ID(N'pos.OfflineInvoiceFifoSequence', N'U') IS NULL
BEGIN
    CREATE TABLE pos.OfflineInvoiceFifoSequence
    (
        TerminalId      NVARCHAR(50) NOT NULL,
        NextSequence    BIGINT       NOT NULL CONSTRAINT DF_OfflineInvoiceFifoSequence_Next DEFAULT (1),
        CONSTRAINT PK_OfflineInvoiceFifoSequence PRIMARY KEY CLUSTERED (TerminalId),
        CONSTRAINT FK_OfflineInvoiceFifoSequence_Terminals
            FOREIGN KEY (TerminalId) REFERENCES pos.Terminals (TerminalId)
    );
END
GO

/* -------------------------------------------------------------------------
   Local inventory — terminal-side stock for MRA stock operations sync
   ------------------------------------------------------------------------- */
IF OBJECT_ID(N'pos.LocalInventory', N'U') IS NULL
BEGIN
    CREATE TABLE pos.LocalInventory
    (
        LocalInventoryId    BIGINT          IDENTITY(1,1) NOT NULL,
        TerminalId          NVARCHAR(50)    NOT NULL,
        ProductCode         NVARCHAR(50)    NOT NULL,
        Description         NVARCHAR(500)   NOT NULL,
        UnitPrice           DECIMAL(18, 4)  NOT NULL,
        QuantityOnHand      DECIMAL(18, 4)  NOT NULL CONSTRAINT DF_LocalInventory_Qty DEFAULT (0),
        TaxRateId           NVARCHAR(20)    NOT NULL,
        IsProduct           BIT             NOT NULL CONSTRAINT DF_LocalInventory_IsProduct DEFAULT (1),
        IsActive            BIT             NOT NULL CONSTRAINT DF_LocalInventory_IsActive DEFAULT (1),
        LastModifiedAtUtc   DATETIME2(7)    NOT NULL CONSTRAINT DF_LocalInventory_LastModified DEFAULT (SYSUTCDATETIME()),
        RowVersion          ROWVERSION      NOT NULL,
        CONSTRAINT PK_LocalInventory PRIMARY KEY CLUSTERED (LocalInventoryId),
        CONSTRAINT FK_LocalInventory_Terminals
            FOREIGN KEY (TerminalId) REFERENCES pos.Terminals (TerminalId),
        CONSTRAINT UQ_LocalInventory_Terminal_ProductCode
            UNIQUE (TerminalId, ProductCode)
    );

    CREATE INDEX IX_LocalInventory_Terminal_Active
        ON pos.LocalInventory (TerminalId, ProductCode)
        WHERE IsActive = 1;
END
GO

/* Helper: allocate next FIFO sequence atomically */
CREATE OR ALTER PROCEDURE pos.usp_AllocateOfflineFifoSequence
    @TerminalId NVARCHAR(50),
    @FifoSequence BIGINT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @Allocated TABLE (Seq BIGINT NOT NULL);

    BEGIN TRAN;

    IF NOT EXISTS (SELECT 1 FROM pos.OfflineInvoiceFifoSequence WITH (UPDLOCK, HOLDLOCK) WHERE TerminalId = @TerminalId)
    BEGIN
        INSERT INTO pos.OfflineInvoiceFifoSequence (TerminalId, NextSequence) VALUES (@TerminalId, 2);
        SET @FifoSequence = 1;
    END
    ELSE
    BEGIN
        UPDATE pos.OfflineInvoiceFifoSequence
        SET NextSequence = NextSequence + 1
        OUTPUT deleted.NextSequence INTO @Allocated(Seq)
        WHERE TerminalId = @TerminalId;

        SELECT @FifoSequence = Seq FROM @Allocated;
    END

    COMMIT TRAN;
END
GO

PRINT N'MRA POS schema Phase 1 applied successfully.';
GO
