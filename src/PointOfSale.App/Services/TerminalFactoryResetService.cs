using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.App.Services;

public interface ITerminalFactoryResetService
{
    /// <summary>
    /// Erases terminal identity, receipts, products, MRA caches, and registry mirrors
    /// so the till can run first-run activation again. Keeps operators and schema flags.
    /// </summary>
    Task<TerminalFactoryResetResult> ResetAsync(CancellationToken cancellationToken = default);
}

public sealed record TerminalFactoryResetResult(bool Success, string Message);

/// <summary>
/// Admin factory reset — brand-new terminal wipe including fiscal receipts and local catalog.
/// </summary>
public sealed class TerminalFactoryResetService : ITerminalFactoryResetService
{
    public const string RegistryKeyPath = @"Software\AlbertRetail\AlbertRetailTerminal";

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ILogger<TerminalFactoryResetService> _logger;

    public TerminalFactoryResetService(
        ISqlConnectionFactory connectionFactory,
        ILogger<TerminalFactoryResetService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<TerminalFactoryResetResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _connectionFactory
                .CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var transaction = (SqlTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await ExecuteWipeAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            ClearRegistryMirror();

            _logger.LogWarning("Admin factory reset completed — terminal identity, receipts, and products cleared.");
            return new TerminalFactoryResetResult(
                true,
                "Terminal reset complete. Receipts, products, and MRA/license identity were erased. " +
                "Sign in again and complete first-run activation with a new TAC.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin factory reset failed.");
            return new TerminalFactoryResetResult(false, $"Terminal reset failed: {ex.Message}");
        }
    }

    private static async Task ExecuteWipeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        // Order: child/history tables first, then identity configs.
        string[] deletes =
        [
            "IF OBJECT_ID(N'dbo.OfflineInvoiceQueue', N'U') IS NOT NULL DELETE FROM dbo.OfflineInvoiceQueue;",
            "IF OBJECT_ID(N'dbo.SalesTransactions', N'U') IS NOT NULL DELETE FROM dbo.SalesTransactions;",
            "IF OBJECT_ID(N'dbo.FiscalArchivePackages', N'U') IS NOT NULL DELETE FROM dbo.FiscalArchivePackages;",
            "IF OBJECT_ID(N'dbo.FiscalYearArchives', N'U') IS NOT NULL DELETE FROM dbo.FiscalYearArchives;",
            "IF OBJECT_ID(N'dbo.LoyaltyLedger', N'U') IS NOT NULL DELETE FROM dbo.LoyaltyLedger;",
            "IF OBJECT_ID(N'dbo.InventoryStockAlerts', N'U') IS NOT NULL DELETE FROM dbo.InventoryStockAlerts;",
            "IF OBJECT_ID(N'dbo.LocalInventory', N'U') IS NOT NULL DELETE FROM dbo.LocalInventory;",
            "IF OBJECT_ID(N'dbo.TerminalHeartbeat', N'U') IS NOT NULL DELETE FROM dbo.TerminalHeartbeat;",
            "IF OBJECT_ID(N'dbo.TerminalLicenseActivation', N'U') IS NOT NULL DELETE FROM dbo.TerminalLicenseActivation;",
            "IF OBJECT_ID(N'dbo.Terminals', N'U') IS NOT NULL DELETE FROM dbo.Terminals;",
            "IF OBJECT_ID(N'dbo.CashierShifts', N'U') IS NOT NULL DELETE FROM dbo.CashierShifts;",
            "IF OBJECT_ID(N'dbo.FinancialClosures', N'U') IS NOT NULL DELETE FROM dbo.FinancialClosures;",
            "IF OBJECT_ID(N'dbo.HeadOfficeSyncOutbox', N'U') IS NOT NULL DELETE FROM dbo.HeadOfficeSyncOutbox;",
            "IF OBJECT_ID(N'dbo.MultiTerminalSyncLedger', N'U') IS NOT NULL DELETE FROM dbo.MultiTerminalSyncLedger;",
            "IF OBJECT_ID(N'dbo.MultiTerminalSyncCursor', N'U') IS NOT NULL DELETE FROM dbo.MultiTerminalSyncCursor;",
            "IF OBJECT_ID(N'dbo.MraApiAuditLog', N'U') IS NOT NULL DELETE FROM dbo.MraApiAuditLog;",
            "IF OBJECT_ID(N'dbo.ComplianceAuditLog', N'U') IS NOT NULL DELETE FROM dbo.ComplianceAuditLog;",
            "IF OBJECT_ID(N'dbo.SecurityAuditLog', N'U') IS NOT NULL DELETE FROM dbo.SecurityAuditLog;",
            "IF OBJECT_ID(N'dbo.DiagnosticTelemetryEvents', N'U') IS NOT NULL DELETE FROM dbo.DiagnosticTelemetryEvents;",
            """
            DELETE FROM dbo.Configurations
            WHERE ConfigKey LIKE N'mra.%'
               OR ConfigKey LIKE N'Mra.%'
               OR ConfigKey LIKE N'deployment.%'
               OR ConfigKey LIKE N'pos.terminal.%'
               OR ConfigKey LIKE N'%.invoice.sequence%'
               OR ConfigKey LIKE N'mra.sales.invoiceSequence.%'
               OR ConfigKey LIKE N'mra.utilities.%'
               OR ConfigKey IN (
                    N'FirstRun.Completed',
                    N'FirstRun.CompletedUtc',
                    N'FirstRun.MraEnvironment',
                    N'pos.firstRun.completed',
                    N'Terminal.License.Activated',
                    N'Terminal.License.Payload',
                    N'Catalog.DemoSeedApplied');
            """,
            """
            DELETE FROM dbo.Configurations
            WHERE ConfigKey LIKE N'Terminal.License.%'
              AND ConfigKey <> N'Terminal.License.RequireActivation';
            """,
            """
            MERGE dbo.Configurations AS t
            USING (SELECT N'FirstRun.SetupWizardAvailable' AS ConfigKey, N'true' AS ConfigJson) AS s
            ON t.ConfigKey = s.ConfigKey
            WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
            WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt)
                 VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());
            """,
            """
            MERGE dbo.Configurations AS t
            USING (SELECT N'Terminal.License.RequireActivation' AS ConfigKey, N'true' AS ConfigJson) AS s
            ON t.ConfigKey = s.ConfigKey
            WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
            WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt)
                 VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());
            """,
            """
            MERGE dbo.Configurations AS t
            USING (SELECT N'Catalog.DemoSeedApplied' AS ConfigKey, N'true' AS ConfigJson) AS s
            ON t.ConfigKey = s.ConfigKey
            WHEN MATCHED THEN UPDATE SET ConfigJson = s.ConfigJson, UpdatedAt = GETUTCDATE()
            WHEN NOT MATCHED THEN INSERT (ConfigKey, ConfigJson, UpdatedAt)
                 VALUES (s.ConfigKey, s.ConfigJson, GETUTCDATE());
            """
        ];

        foreach (var sql in deletes)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandType = CommandType.Text;
            command.CommandText = sql;
            command.CommandTimeout = 120;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Reseed receipt identities when present.
        await ReseedIdentityAsync(connection, transaction, "dbo.OfflineInvoiceQueue", cancellationToken)
            .ConfigureAwait(false);
        await ReseedIdentityAsync(connection, transaction, "dbo.SalesTransactions", cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ReseedIdentityAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            IF EXISTS (
                SELECT 1 FROM sys.identity_columns
                WHERE object_id = OBJECT_ID(N'{tableName}'))
            BEGIN
                DBCC CHECKIDENT ('{tableName}', RESEED, 0);
            END
            """;
        command.CommandTimeout = 60;
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // Non-fatal — wipe already succeeded.
        }
    }

    private static void ClearRegistryMirror()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryKeyPath, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(@"Software\AlbertRetail", writable: true);
            if (parent is not null && parent.GetSubKeyNames().Length == 0)
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\AlbertRetail", throwOnMissingSubKey: false);
            }
        }
        catch
        {
            // Registry mirror is best-effort; SQL wipe is authoritative.
        }
    }
}
