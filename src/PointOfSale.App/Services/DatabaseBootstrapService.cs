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

                CREATE INDEX IX_OfflineInvoiceQueue_PendingFifo
                    ON dbo.OfflineInvoiceQueue (Status, CreatedAt, Id)
                    WHERE Status = N'PENDING';
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
                    Trigger         VARCHAR(40)     NOT NULL,
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
