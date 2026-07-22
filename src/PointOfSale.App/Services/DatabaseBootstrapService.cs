using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;

namespace PointOfSale.App.Services;

public interface IDatabaseBootstrapService
{
    Task EnsureDatabaseReadyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Provisions PointOfSale SQL Express schema on first launch (idempotent).
/// Creates Terminals, Configurations, OfflineInvoiceQueue, LocalInventory (+ later migrations).
/// </summary>
public sealed class DatabaseBootstrapService : IDatabaseBootstrapService
{
    public const string SchemaVersionConfigKey = "Schema.Version";

    private readonly string _connectionString;
    private readonly DatabaseBootstrapOptions _options;
    private readonly ILogger<DatabaseBootstrapService> _logger;

    public DatabaseBootstrapService(
        IConfiguration configuration,
        IOptions<DatabaseBootstrapOptions> options,
        ILogger<DatabaseBootstrapService> logger)
    {
        _connectionString = configuration.GetConnectionString("PosDatabase")
            ?? throw new InvalidOperationException("Connection string 'PosDatabase' is missing.");
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureDatabaseReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        await EnsureSqlExpressReachableAsync(cancellationToken).ConfigureAwait(false);
        await EnsureDatabaseExistsAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await EnsureCoreTablesAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
        await UpsertSchemaVersionAsync(connection, _options.TargetSchemaVersion, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "SQL Express schema ready (version {SchemaVersion}).",
            _options.TargetSchemaVersion);
    }

    private async Task EnsureSqlExpressReachableAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString)
        {
            InitialCatalog = "master",
            ConnectTimeout = 8
        };

        try
        {
            await using var connection = new SqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Cannot reach SQL Server Express ({_options.RequiredInstanceHint}). " +
                "Install/start the SQLEXPRESS instance, then relaunch Albert Retail Terminal.",
                ex);
        }
    }

    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("PosDatabase connection string must include Initial Catalog / Database.");
        }

        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            IF DB_ID(@DatabaseName) IS NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@DatabaseName);
                EXEC(@sql);
            END
            """;
        command.Parameters.Add(new SqlParameter("@DatabaseName", SqlDbType.NVarChar, 128) { Value = databaseName });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureCoreTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.Terminals', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.Terminals
                (
                    TerminalId      VARCHAR(50)     NOT NULL,
                    BranchCode      VARCHAR(50)     NULL,
                    ActivationState VARCHAR(20)     NOT NULL
                        CONSTRAINT CK_Terminals_ActivationState
                        CHECK (ActivationState IN (
                            N'NotActivated', N'PendingConfirmation', N'Activated', N'Deactivated')),
                    SecretKey       NVARCHAR(MAX)   NULL,
                    LastSyncedAt    DATETIME        NULL,
                    CONSTRAINT PK_Terminals PRIMARY KEY CLUSTERED (TerminalId)
                );
            END;

            IF OBJECT_ID(N'dbo.Configurations', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.Configurations
                (
                    ConfigKey   VARCHAR(100)    NOT NULL,
                    ConfigJson  NVARCHAR(MAX)   NOT NULL,
                    UpdatedAt   DATETIME        NOT NULL CONSTRAINT DF_Configurations_UpdatedAt DEFAULT (GETUTCDATE()),
                    CONSTRAINT PK_Configurations PRIMARY KEY CLUSTERED (ConfigKey)
                );
            END;

            IF OBJECT_ID(N'dbo.OfflineInvoiceQueue', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.OfflineInvoiceQueue
                (
                    Id              INT             IDENTITY(1,1) NOT NULL,
                    PayloadJson     NVARCHAR(MAX)   NOT NULL,
                    CreatedAt       DATETIME        NOT NULL CONSTRAINT DF_OfflineInvoiceQueue_CreatedAt DEFAULT (GETUTCDATE()),
                    Status          VARCHAR(20)     NOT NULL
                        CONSTRAINT CK_OfflineInvoiceQueue_Status
                        CHECK (Status IN (N'PENDING', N'SYNCING', N'SYNCED', N'QUARANTINED')),
                    RetryCount      INT             NOT NULL CONSTRAINT DF_OfflineInvoiceQueue_RetryCount DEFAULT (0),
                    NextRetryTime   DATETIME        NULL,
                    ErrorMessage    NVARCHAR(MAX)   NULL,
                    FiscalResponseJson NVARCHAR(MAX) NULL,
                    CONSTRAINT PK_OfflineInvoiceQueue PRIMARY KEY CLUSTERED (Id)
                );

                CREATE INDEX IX_OfflineInvoiceQueue_MraSyncPoll
                    ON dbo.OfflineInvoiceQueue (Status, CreatedAt, Id)
                    INCLUDE (RetryCount, NextRetryTime, ErrorMessage);
            END;

            IF OBJECT_ID(N'dbo.LocalInventory', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.LocalInventory
                (
                    ProductId       VARCHAR(50)     NOT NULL,
                    ProductCode     VARCHAR(100)    NOT NULL,
                    Name            NVARCHAR(200)   NOT NULL,
                    UnitPrice       DECIMAL(18, 2)  NOT NULL,
                    StockQuantity   DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_LocalInventory_Stock DEFAULT (0),
                    HsCode          VARCHAR(50)     NULL,
                    UnitOfMeasure   VARCHAR(20)     NULL,
                    TaxRateId       VARCHAR(20)     NULL,
                    CONSTRAINT PK_LocalInventory PRIMARY KEY CLUSTERED (ProductId)
                );

                CREATE UNIQUE INDEX UX_LocalInventory_ProductCode ON dbo.LocalInventory (ProductCode);
            END;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureMigrationsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            IF COL_LENGTH(N'dbo.LocalInventory', N'TaxRateId') IS NULL
                ALTER TABLE dbo.LocalInventory ADD TaxRateId VARCHAR(20) NULL;

            IF COL_LENGTH(N'dbo.OfflineInvoiceQueue', N'FiscalResponseJson') IS NULL
                ALTER TABLE dbo.OfflineInvoiceQueue ADD FiscalResponseJson NVARCHAR(MAX) NULL;

            IF OBJECT_ID(N'dbo.MraApiAuditLog', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.MraApiAuditLog
                (
                    AuditId         BIGINT          IDENTITY(1,1) NOT NULL,
                    CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_MraApiAuditLog_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
                    HttpMethod      NVARCHAR(10)    NOT NULL,
                    RequestPath     NVARCHAR(500)   NOT NULL,
                    HttpStatusCode  INT             NULL,
                    DurationMs      INT             NOT NULL,
                    RequestBody     NVARCHAR(MAX)   NULL,
                    ResponseBody    NVARCHAR(MAX)   NULL,
                    IsSuccess       BIT             NOT NULL,
                    ErrorMessage    NVARCHAR(2000)  NULL,
                    CONSTRAINT PK_MraApiAuditLog PRIMARY KEY CLUSTERED (AuditId)
                );

                CREATE INDEX IX_MraApiAuditLog_CreatedAtUtc ON dbo.MraApiAuditLog (CreatedAtUtc DESC);
            END;

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
            END;

            IF OBJECT_ID(N'dbo.ShiftCashMovements', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.ShiftCashMovements
                (
                    MovementId      INT             IDENTITY(1,1) NOT NULL,
                    ShiftId         INT             NOT NULL,
                    MovementType    VARCHAR(20)     NOT NULL
                        CONSTRAINT CK_ShiftCashMovements_Type CHECK (MovementType IN (N'CashIn', N'CashOut', N'CashDrop')),
                    Amount          DECIMAL(18, 2)  NOT NULL,
                    Reason          NVARCHAR(200)   NULL,
                    CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_ShiftCashMovements_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
                    CONSTRAINT PK_ShiftCashMovements PRIMARY KEY CLUSTERED (MovementId),
                    CONSTRAINT FK_ShiftCashMovements_Shift FOREIGN KEY (ShiftId) REFERENCES dbo.CashierShifts (ShiftId)
                );
            END;

            IF OBJECT_ID(N'dbo.HeadOfficeSyncOutbox', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.HeadOfficeSyncOutbox
                (
                    OutboxId        BIGINT          IDENTITY(1,1) NOT NULL,
                    PayloadType     VARCHAR(40)     NOT NULL,
                    CorrelationKey  NVARCHAR(200)   NOT NULL,
                    PlainJson       NVARCHAR(MAX)   NOT NULL,
                    Status          VARCHAR(20)     NOT NULL
                        CONSTRAINT CK_HeadOfficeSyncOutbox_Status
                        CHECK (Status IN (N'Pending', N'Uploading', N'Uploaded', N'Failed')),
                    CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_HeadOfficeSyncOutbox_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
                    UploadedAtUtc   DATETIME2(7)    NULL,
                    ErrorMessage    NVARCHAR(2000)  NULL,
                    AttemptCount    INT             NOT NULL CONSTRAINT DF_HeadOfficeSyncOutbox_AttemptCount DEFAULT (0),
                    CONSTRAINT PK_HeadOfficeSyncOutbox PRIMARY KEY CLUSTERED (OutboxId)
                );

                CREATE INDEX IX_HeadOfficeSyncOutbox_Status_Created
                    ON dbo.HeadOfficeSyncOutbox (Status, CreatedAtUtc, OutboxId);
            END;

            IF COL_LENGTH(N'dbo.LocalInventory', N'CatalogSource') IS NULL
                ALTER TABLE dbo.LocalInventory ADD CatalogSource VARCHAR(20) NOT NULL
                    CONSTRAINT DF_LocalInventory_CatalogSource DEFAULT (N'Local');

            IF COL_LENGTH(N'dbo.LocalInventory', N'HeadOfficeRevisionUtc') IS NULL
                ALTER TABLE dbo.LocalInventory ADD HeadOfficeRevisionUtc DATETIME2(7) NULL;

            IF COL_LENGTH(N'dbo.LocalInventory', N'LastReplicatedAtUtc') IS NULL
                ALTER TABLE dbo.LocalInventory ADD LastReplicatedAtUtc DATETIME2(7) NULL;

            IF OBJECT_ID(N'dbo.DatabaseBackupHistory', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.DatabaseBackupHistory
                (
                    BackupId        BIGINT          IDENTITY(1,1) NOT NULL,
                    CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_DatabaseBackupHistory_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
                    [Trigger]       VARCHAR(40)     NOT NULL,
                    BackupFilePath  NVARCHAR(500)   NOT NULL,
                    Sha256Checksum  VARCHAR(64)     NOT NULL CONSTRAINT DF_DatabaseBackupHistory_Sha DEFAULT (N''),
                    BackupBytes     BIGINT          NOT NULL CONSTRAINT DF_DatabaseBackupHistory_Bytes DEFAULT (0),
                    Success         BIT             NOT NULL,
                    ErrorMessage    NVARCHAR(2000)  NULL,
                    CONSTRAINT PK_DatabaseBackupHistory PRIMARY KEY CLUSTERED (BackupId)
                );

                CREATE INDEX IX_DatabaseBackupHistory_CreatedAtUtc
                    ON dbo.DatabaseBackupHistory (CreatedAtUtc DESC, BackupId DESC);
            END;

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
            END;

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
            END;

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
            END;

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
            END;

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
            END;

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
            END;

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
            END;

            IF COL_LENGTH(N'dbo.LocalInventory', N'MinReorderQty') IS NULL
                ALTER TABLE dbo.LocalInventory ADD MinReorderQty DECIMAL(18, 2) NOT NULL
                    CONSTRAINT DF_LocalInventory_MinReorderQty DEFAULT (0);

            IF COL_LENGTH(N'dbo.LocalInventory', N'MaxStockCapacity') IS NULL
                ALTER TABLE dbo.LocalInventory ADD MaxStockCapacity DECIMAL(18, 2) NOT NULL
                    CONSTRAINT DF_LocalInventory_MaxStockCapacity DEFAULT (0);

            IF COL_LENGTH(N'dbo.LocalInventory', N'SupplierCode') IS NULL
                ALTER TABLE dbo.LocalInventory ADD SupplierCode NVARCHAR(40) NULL;

            IF COL_LENGTH(N'dbo.LocalInventory', N'SupplierName') IS NULL
                ALTER TABLE dbo.LocalInventory ADD SupplierName NVARCHAR(150) NULL;

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
            END;

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
            END;

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
            END;

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
            END;

            IF COL_LENGTH(N'dbo.LocalInventory', N'AverageUnitCost') IS NULL
                ALTER TABLE dbo.LocalInventory ADD AverageUnitCost DECIMAL(18, 2) NOT NULL
                    CONSTRAINT DF_LocalInventory_AverageUnitCost DEFAULT (0);

            IF COL_LENGTH(N'dbo.LocalInventory', N'MarkupPercent') IS NULL
                ALTER TABLE dbo.LocalInventory ADD MarkupPercent DECIMAL(9, 4) NOT NULL
                    CONSTRAINT DF_LocalInventory_MarkupPercent DEFAULT (0);

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
            END;

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
            END;

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
            END;

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
            END;

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
            END;

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
            END;

            IF OBJECT_ID(N'dbo.FiscalYearArchives', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.FiscalYearArchives
                (
                    ArchiveId                     BIGINT          IDENTITY(1,1) NOT NULL,
                    FiscalYear                    INT             NOT NULL,
                    PeriodStart                   DATE            NOT NULL,
                    PeriodEnd                     DATE            NOT NULL,
                    RolledOverAtUtc               DATETIME2(7)    NOT NULL CONSTRAINT DF_FiscalYearArchives_RolledOverAtUtc DEFAULT (SYSUTCDATETIME()),
                    PrimarySupervisorUsername     NVARCHAR(80)    NOT NULL,
                    SecondarySupervisorUsername   NVARCHAR(80)    NOT NULL,
                    TotalGrossSalesMwk            DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FiscalYearArchives_Gross DEFAULT (0),
                    TotalVatCollectedMwk          DECIMAL(18, 2)  NOT NULL CONSTRAINT DF_FiscalYearArchives_Vat DEFAULT (0),
                    ExpectedClosureDays           INT             NOT NULL CONSTRAINT DF_FiscalYearArchives_ExpectedDays DEFAULT (0),
                    ClosedDays                    INT             NOT NULL CONSTRAINT DF_FiscalYearArchives_ClosedDays DEFAULT (0),
                    SyncedInvoiceCount            INT             NOT NULL CONSTRAINT DF_FiscalYearArchives_Synced DEFAULT (0),
                    ManifestSha256                CHAR(64)        NOT NULL,
                    ManifestHmacSha512            CHAR(128)       NOT NULL,
                    ArchiveFilePath               NVARCHAR(500)   NOT NULL,
                    ArchiveBytes                  BIGINT          NOT NULL CONSTRAINT DF_FiscalYearArchives_Bytes DEFAULT (0),
                    CryptographicVerificationPassed BIT             NOT NULL CONSTRAINT DF_FiscalYearArchives_Crypto DEFAULT (0),
                    Status                        VARCHAR(20)     NOT NULL CONSTRAINT DF_FiscalYearArchives_Status DEFAULT ('Locked'),
                    Notes                         NVARCHAR(1000)  NULL,
                    CONSTRAINT PK_FiscalYearArchives PRIMARY KEY CLUSTERED (ArchiveId)
                );

                CREATE UNIQUE INDEX UX_FiscalYearArchives_FiscalYear_Locked
                    ON dbo.FiscalYearArchives (FiscalYear)
                    WHERE Status = 'Locked';

                CREATE INDEX IX_FiscalYearArchives_RolledOver
                    ON dbo.FiscalYearArchives (RolledOverAtUtc DESC, ArchiveId DESC);
            END;

            IF OBJECT_ID(N'dbo.FiscalArchivePackages', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.FiscalArchivePackages
                (
                    PackageId           BIGINT          IDENTITY(1,1) NOT NULL,
                    CreatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_FiscalArchivePackages_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
                    PackageType         VARCHAR(40)     NOT NULL,
                    PeriodStartUtc      DATETIME2(7)    NOT NULL,
                    PeriodEndUtc        DATETIME2(7)    NOT NULL,
                    FilePath            NVARCHAR(500)   NOT NULL,
                    FileBytes           BIGINT          NOT NULL CONSTRAINT DF_FiscalArchivePackages_FileBytes DEFAULT (0),
                    ContentSha256       CHAR(64)        NOT NULL,
                    TriggeredByUsername NVARCHAR(80)    NOT NULL,
                    DualKeyProtected    BIT             NOT NULL CONSTRAINT DF_FiscalArchivePackages_DualKey DEFAULT (1),
                    CONSTRAINT PK_FiscalArchivePackages PRIMARY KEY CLUSTERED (PackageId)
                );

                CREATE INDEX IX_FiscalArchivePackages_Created
                    ON dbo.FiscalArchivePackages (CreatedAtUtc DESC, PackageId DESC);
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Schema.Phase28Applied')
            BEGIN
                IF OBJECT_ID(N'dbo.SalesTransactions', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.SalesTransactions
                    (
                        TransactionId       BIGINT          IDENTITY(1,1) NOT NULL,
                        QueueId             INT             NULL,
                        BusinessDate        DATE            NOT NULL,
                        InvoiceNumber       VARCHAR(50)     NOT NULL,
                        PaymentMethod       VARCHAR(20)     NOT NULL,
                        GrossAmountMwk      DECIMAL(18, 2)  NOT NULL,
                        VatAmountMwk        DECIMAL(18, 2)  NOT NULL,
                        ShiftId             INT             NULL,
                        SyncStatus          VARCHAR(20)     NOT NULL
                            CONSTRAINT CK_SalesTransactions_SyncStatus
                            CHECK (SyncStatus IN ('PENDING', 'SYNCING', 'SYNCED', 'QUARANTINED')),
                        CreatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_SalesTransactions_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
                        CONSTRAINT PK_SalesTransactions PRIMARY KEY CLUSTERED (TransactionId)
                    );

                    CREATE UNIQUE INDEX UX_SalesTransactions_InvoiceNumber ON dbo.SalesTransactions (InvoiceNumber);
                    CREATE INDEX IX_SalesTransactions_BusinessDate_Reporting
                        ON dbo.SalesTransactions (BusinessDate DESC, PaymentMethod, TransactionId)
                        INCLUDE (GrossAmountMwk, VatAmountMwk, SyncStatus, ShiftId);
                    CREATE INDEX IX_SalesTransactions_CreatedAtUtc
                        ON dbo.SalesTransactions (CreatedAtUtc DESC, TransactionId DESC);
                END;

                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OfflineInvoiceQueue_PendingFifo' AND object_id = OBJECT_ID(N'dbo.OfflineInvoiceQueue'))
                    DROP INDEX IX_OfflineInvoiceQueue_PendingFifo ON dbo.OfflineInvoiceQueue;

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OfflineInvoiceQueue_MraSyncPoll' AND object_id = OBJECT_ID(N'dbo.OfflineInvoiceQueue'))
                    CREATE INDEX IX_OfflineInvoiceQueue_MraSyncPoll
                        ON dbo.OfflineInvoiceQueue (Status ASC, NextRetryTime ASC, CreatedAt ASC, Id ASC)
                        INCLUDE (RetryCount, ErrorMessage);

                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OfflineInvoiceQueue_SyncedReporting' AND object_id = OBJECT_ID(N'dbo.OfflineInvoiceQueue'))
                    CREATE INDEX IX_OfflineInvoiceQueue_SyncedReporting
                        ON dbo.OfflineInvoiceQueue (Status ASC, CreatedAt DESC, Id DESC)
                        INCLUDE (FiscalResponseJson);

                IF OBJECT_ID(N'dbo.DatabaseMaintenanceLog', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.DatabaseMaintenanceLog
                    (
                        LogId           BIGINT          IDENTITY(1,1) NOT NULL,
                        ExecutedAtUtc   DATETIME2(7)    NOT NULL CONSTRAINT DF_DatabaseMaintenanceLog_ExecutedAtUtc DEFAULT (SYSUTCDATETIME()),
                        Operation       VARCHAR(40)     NOT NULL,
                        Success         BIT             NOT NULL,
                        Detail          NVARCHAR(2000)  NULL,
                        DurationMs      INT             NOT NULL CONSTRAINT DF_DatabaseMaintenanceLog_DurationMs DEFAULT (0),
                        FragmentedIndexesBefore INT     NULL,
                        DatabaseSizeMbBefore    BIGINT  NULL,
                        CONSTRAINT PK_DatabaseMaintenanceLog PRIMARY KEY CLUSTERED (LogId)
                    );
                    CREATE INDEX IX_DatabaseMaintenanceLog_ExecutedAtUtc
                        ON dbo.DatabaseMaintenanceLog (ExecutedAtUtc DESC, LogId DESC);
                END;

                INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (N'Schema.Phase28Applied', N'true', GETUTCDATE());
            END;

            EXEC(N'
            CREATE OR ALTER PROCEDURE dbo.usp_PurgeExpiredDiagnosticTelemetry
                @RetentionDays INT
            AS
            BEGIN
                SET NOCOUNT ON;
                IF @RetentionDays < 1 SET @RetentionDays = 1;
                DECLARE @Cutoff DATETIME2(7) = DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME());
                DELETE FROM dbo.DiagnosticTelemetryEvents WHERE CreatedAtUtc < @Cutoff;
                SELECT @@ROWCOUNT AS DeletedRows;
            END');

            IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Schema.Phase29Applied')
            BEGIN
                IF OBJECT_ID(N'dbo.ComplianceAuditLog', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.ComplianceAuditLog
                    (
                        EntryId             BIGINT          IDENTITY(1,1) NOT NULL,
                        CreatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_ComplianceAuditLog_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
                        Category            VARCHAR(40)     NOT NULL,
                        Action              VARCHAR(80)     NOT NULL,
                        OperatorUsername    NVARCHAR(80)    NOT NULL CONSTRAINT DF_ComplianceAuditLog_Operator DEFAULT (N'system'),
                        CorrelationId       VARCHAR(100)    NULL,
                        Detail              NVARCHAR(2000)  NOT NULL,
                        Success             BIT             NOT NULL,
                        PreviousHash        CHAR(64)        NOT NULL,
                        EntryHash           CHAR(64)        NOT NULL,
                        CONSTRAINT PK_ComplianceAuditLog PRIMARY KEY CLUSTERED (EntryId)
                    );
                    CREATE INDEX IX_ComplianceAuditLog_CreatedAtUtc
                        ON dbo.ComplianceAuditLog (CreatedAtUtc DESC, EntryId DESC);
                    CREATE INDEX IX_ComplianceAuditLog_Category
                        ON dbo.ComplianceAuditLog (Category, CreatedAtUtc DESC);
                END;
                INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (N'Schema.Phase29Applied', N'true', GETUTCDATE());
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Schema.Phase30Applied')
            BEGIN
                IF OBJECT_ID(N'dbo.Seq_MultiTerminalSync', N'SO') IS NULL
                BEGIN
                    CREATE SEQUENCE dbo.Seq_MultiTerminalSync AS BIGINT START WITH 1 INCREMENT BY 1;
                END;

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
                END;

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
                END;

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
                END;

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
                END;

                INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (N'Schema.Phase30Applied', N'true', GETUTCDATE());
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Schema.Phase31Applied')
            BEGIN
                IF OBJECT_ID(N'dbo.UiWorkspacePreferences', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.UiWorkspacePreferences
                    (
                        PreferenceId        INT             IDENTITY(1,1) NOT NULL,
                        OperatorId          INT             NOT NULL,
                        PreferredShell      VARCHAR(20)     NOT NULL CONSTRAINT DF_UiWorkspacePreferences_Shell DEFAULT (N'Cashier'),
                        ThemeMode           VARCHAR(20)     NOT NULL CONSTRAINT DF_UiWorkspacePreferences_Theme DEFAULT (N'Light'),
                        UpdatedAtUtc        DATETIME2(7)    NOT NULL CONSTRAINT DF_UiWorkspacePreferences_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
                        CONSTRAINT PK_UiWorkspacePreferences PRIMARY KEY CLUSTERED (PreferenceId),
                        CONSTRAINT UQ_UiWorkspacePreferences_Operator UNIQUE (OperatorId)
                    );
                END;

                MERGE dbo.Configurations AS target
                USING (SELECT N'Fiscal.StandardVatRatePercent' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'Fiscal.StandardVatRatePercent', N'17.5', GETUTCDATE())
                WHEN MATCHED THEN
                    UPDATE SET ConfigJson = N'17.5', UpdatedAt = GETUTCDATE();

                MERGE dbo.Configurations AS target
                USING (SELECT N'Ui.ThemeMode' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'Ui.ThemeMode', N'Light', GETUTCDATE());

                INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (N'Schema.Phase31Applied', N'true', GETUTCDATE());
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Schema.Phase32Applied')
            BEGIN
                IF OBJECT_ID(N'dbo.TerminalLicenseActivation', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.TerminalLicenseActivation
                    (
                        ActivationId        INT             IDENTITY(1,1) NOT NULL,
                        LicenseKeyHash      CHAR(64)        NOT NULL,
                        MaskedLicenseKey    VARCHAR(32)     NOT NULL,
                        ActivatedAtUtc      DATETIME2(7)    NOT NULL CONSTRAINT DF_TerminalLicenseActivation_ActivatedAtUtc DEFAULT (SYSUTCDATETIME()),
                        ActivatedByHost     NVARCHAR(128)   NULL,
                        IsActive            BIT             NOT NULL CONSTRAINT DF_TerminalLicenseActivation_IsActive DEFAULT (1),
                        CONSTRAINT PK_TerminalLicenseActivation PRIMARY KEY CLUSTERED (ActivationId)
                    );
                    CREATE UNIQUE INDEX UX_TerminalLicenseActivation_Hash
                        ON dbo.TerminalLicenseActivation (LicenseKeyHash)
                        WHERE IsActive = 1;
                END;

                MERGE dbo.Configurations AS target
                USING (SELECT N'Terminal.License.RequireActivation' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'Terminal.License.RequireActivation', N'true', GETUTCDATE());

                INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (N'Schema.Phase32Applied', N'true', GETUTCDATE());
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Schema.Phase33Applied')
            BEGIN
                IF COL_LENGTH(N'dbo.DatabaseBackupHistory', N'VerifiedSha256') IS NULL
                   AND OBJECT_ID(N'dbo.DatabaseBackupHistory', N'U') IS NOT NULL
                BEGIN
                    ALTER TABLE dbo.DatabaseBackupHistory
                        ADD VerifiedSha256 BIT NOT NULL
                            CONSTRAINT DF_DatabaseBackupHistory_VerifiedSha256 DEFAULT (0);
                END;

                MERGE dbo.Configurations AS target
                USING (SELECT N'Backup.EndOfDayHourLocal' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'Backup.EndOfDayHourLocal', N'21', GETUTCDATE());

                MERGE dbo.Configurations AS target
                USING (SELECT N'Backup.RetentionDays' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'Backup.RetentionDays', N'30', GETUTCDATE());

                MERGE dbo.Configurations AS target
                USING (SELECT N'Hardware.FaultToleranceEnabled' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'Hardware.FaultToleranceEnabled', N'true', GETUTCDATE());

                INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (N'Schema.Phase33Applied', N'true', GETUTCDATE());
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Schema.Phase34Applied')
            BEGIN
                IF OBJECT_ID(N'dbo.Operators', N'U') IS NOT NULL
                   AND COL_LENGTH(N'dbo.Operators', N'SupervisorPinHash') IS NULL
                BEGIN
                    ALTER TABLE dbo.Operators ADD
                        SupervisorPinHash       NVARCHAR(200)   NULL,
                        SupervisorPinSalt       NVARCHAR(200)   NULL,
                        SupervisorPinIterations INT             NOT NULL
                            CONSTRAINT DF_Operators_SupervisorPinIterations DEFAULT (0);
                END;

                MERGE dbo.Configurations AS target
                USING (SELECT N'Supervisor.DefaultPinSeeded' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'Supervisor.DefaultPinSeeded', N'true', GETUTCDATE());

                INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (N'Schema.Phase34Applied', N'true', GETUTCDATE());
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Schema.Phase35Applied')
            BEGIN
                MERGE dbo.Configurations AS target
                USING (SELECT N'FirstRun.SetupWizardAvailable' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'FirstRun.SetupWizardAvailable', N'true', GETUTCDATE());

                MERGE dbo.Configurations AS target
                USING (SELECT N'Fiscal.StandardVatRatePercent' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'Fiscal.StandardVatRatePercent', N'17.5', GETUTCDATE());

                INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (N'Schema.Phase35Applied', N'true', GETUTCDATE());
            END;

            IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Schema.Phase36Applied')
            BEGIN
                MERGE dbo.Configurations AS target
                USING (SELECT N'Health.MonitorEnabled' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'Health.MonitorEnabled', N'true', GETUTCDATE());

                MERGE dbo.Configurations AS target
                USING (SELECT N'Health.QueueBacklogWarnCount' AS ConfigKey) AS source
                ON target.ConfigKey = source.ConfigKey
                WHEN NOT MATCHED THEN
                    INSERT (ConfigKey, ConfigJson, UpdatedAt)
                    VALUES (N'Health.QueueBacklogWarnCount', N'25', GETUTCDATE());

                INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (N'Schema.Phase36Applied', N'true', GETUTCDATE());
            END;

            /* Phase 37 — reserved-keyword safety: [Trigger] column already delimited in CREATE TABLE.
               Record migration so first-run / sqlcmd InitialSetup stays aligned with in-app bootstrap. */
            IF NOT EXISTS (SELECT 1 FROM dbo.Configurations WHERE ConfigKey = N'Schema.Phase37Applied')
            BEGIN
                IF OBJECT_ID(N'dbo.DatabaseBackupHistory', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.DatabaseBackupHistory
                    (
                        BackupId        BIGINT          IDENTITY(1,1) NOT NULL,
                        CreatedAtUtc    DATETIME2(7)    NOT NULL CONSTRAINT DF_DatabaseBackupHistory_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
                        [Trigger]       VARCHAR(40)     NOT NULL,
                        BackupFilePath  NVARCHAR(500)   NOT NULL,
                        Sha256Checksum  VARCHAR(64)     NOT NULL CONSTRAINT DF_DatabaseBackupHistory_Sha DEFAULT (N''),
                        BackupBytes     BIGINT          NOT NULL CONSTRAINT DF_DatabaseBackupHistory_Bytes DEFAULT (0),
                        Success         BIT             NOT NULL,
                        ErrorMessage    NVARCHAR(2000)  NULL,
                        VerifiedSha256  BIT             NOT NULL CONSTRAINT DF_DatabaseBackupHistory_VerifiedSha256 DEFAULT (0),
                        CONSTRAINT PK_DatabaseBackupHistory PRIMARY KEY CLUSTERED (BackupId)
                    );

                    CREATE INDEX IX_DatabaseBackupHistory_CreatedAtUtc
                        ON dbo.DatabaseBackupHistory (CreatedAtUtc DESC, BackupId DESC);
                END;

                INSERT INTO dbo.Configurations (ConfigKey, ConfigJson, UpdatedAt)
                VALUES (N'Schema.Phase37Applied', N'true', GETUTCDATE());
            END;
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 120;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertSchemaVersionAsync(
        SqlConnection connection,
        int version,
        CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE dbo.Configurations AS target
            USING (SELECT @ConfigKey AS ConfigKey) AS source
            ON target.ConfigKey = source.ConfigKey
            WHEN MATCHED THEN
                UPDATE SET ConfigJson = @ConfigJson, UpdatedAt = GETUTCDATE()
            WHEN NOT MATCHED THEN
                INSERT (ConfigKey, ConfigJson, UpdatedAt) VALUES (@ConfigKey, @ConfigJson, GETUTCDATE());
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@ConfigKey", SchemaVersionConfigKey);
        command.Parameters.AddWithValue("@ConfigJson", version.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
