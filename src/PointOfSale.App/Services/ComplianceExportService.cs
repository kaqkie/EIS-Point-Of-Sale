using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PointOfSale.App.Services;

/// <summary>
/// Packages MRA certification audit logs, schema snapshots, and test reports into a regulatory handoff ZIP.
/// </summary>
public interface IComplianceExportService
{
    Task<ComplianceExportResult> ExportPackageAsync(
        string? terminalId = null,
        CancellationToken cancellationToken = default);
}

public sealed class ComplianceExportService : IComplianceExportService
{
    private readonly IConfiguration _configuration;
    private readonly Compliance.IMraCertificationAuditStore _auditStore;
    private readonly ILogger<ComplianceExportService> _logger;

    public ComplianceExportService(
        IConfiguration configuration,
        Compliance.IMraCertificationAuditStore auditStore,
        ILogger<ComplianceExportService> logger)
    {
        _configuration = configuration;
        _auditStore = auditStore;
        _logger = logger;
    }

    public async Task<ComplianceExportResult> ExportPackageAsync(
        string? terminalId = null,
        CancellationToken cancellationToken = default)
    {
        var resolvedTerminalId = SanitizeFileToken(terminalId ?? await TryResolveTerminalIdAsync(cancellationToken).ConfigureAwait(false) ?? "UNKNOWN");
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var exportRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AlbertRetailTerminal",
            "CompliancePackages");
        Directory.CreateDirectory(exportRoot);

        var zipName = $"MraCompliancePackage_{resolvedTerminalId}_{stamp}.zip";
        var zipPath = Path.Combine(exportRoot, zipName);
        var staging = Path.Combine(Path.GetTempPath(), "ART_Compliance_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);

        try
        {
            await CopyAuditAsync(staging, cancellationToken).ConfigureAwait(false);
            await WriteSchemaSnapshotAsync(staging, cancellationToken).ConfigureAwait(false);
            await WriteExecutionReportAsync(staging, resolvedTerminalId, cancellationToken).ConfigureAwait(false);
            CopySqlScripts(staging);
            CopySerilogSnippets(staging);

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(staging, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            _auditStore.AppendStatus($"Compliance package ready: {zipPath}");
            _logger.LogInformation("MRA compliance package created at {ZipPath}.", zipPath);

            return new ComplianceExportResult(true, zipPath, "Compliance package created successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create compliance package.");
            _auditStore.AppendStatus($"Export failed: {ex.Message}");
            return new ComplianceExportResult(false, zipPath, ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private async Task CopyAuditAsync(string staging, CancellationToken cancellationToken)
    {
        var auditPath = _auditStore.AuditFilePath;
        var dest = Path.Combine(staging, "MraCertificationAudit.json");
        if (File.Exists(auditPath))
        {
            File.Copy(auditPath, dest, overwrite: true);
            return;
        }

        var empty = new Compliance.MraCertificationAuditDocument
        {
            StartedUtc = DateTime.UtcNow,
            CompletedUtc = DateTime.UtcNow,
            OverallResult = "Missing",
            ApplicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0"
        };
        await File.WriteAllTextAsync(
                dest,
                System.Text.Json.JsonSerializer.Serialize(empty, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteSchemaSnapshotAsync(string staging, CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString("PosDatabase");
        var sb = new StringBuilder();
        sb.AppendLine("-- Albert Retail Terminal SQL Express schema snapshot");
        sb.AppendLine($"-- GeneratedUtc: {DateTime.UtcNow:O}");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            sb.AppendLine("-- Connection string PosDatabase is missing.");
            await File.WriteAllTextAsync(Path.Combine(staging, "SchemaSnapshot.sql"), sb.ToString(), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT t.name AS TableName, c.name AS ColumnName, ty.name AS TypeName, c.max_length, c.is_nullable
                FROM sys.tables t
                INNER JOIN sys.columns c ON c.object_id = t.object_id
                INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                WHERE t.is_ms_shipped = 0
                ORDER BY t.name, c.column_id;
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            string? currentTable = null;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var table = reader.GetString(0);
                if (!string.Equals(currentTable, table, StringComparison.Ordinal))
                {
                    currentTable = table;
                    sb.AppendLine();
                    sb.AppendLine($"-- TABLE dbo.{table}");
                }

                sb.AppendLine(
                    $"--   {reader.GetString(1)} {reader.GetString(2)}({reader.GetInt16(3)}) nullable={reader.GetBoolean(4)}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"-- Schema snapshot failed: {ex.Message}");
        }

        await File.WriteAllTextAsync(Path.Combine(staging, "SchemaSnapshot.sql"), sb.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task WriteExecutionReportAsync(string staging, string terminalId, CancellationToken cancellationToken)
    {
        var audit = await _auditStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var report = new StringBuilder();
        report.AppendLine("Albert Retail Terminal — MRA EIS Compliance Execution Report");
        report.AppendLine($"TerminalId: {terminalId}");
        report.AppendLine($"GeneratedLocal: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"ApplicationVersion: {Assembly.GetExecutingAssembly().GetName().Version}");
        report.AppendLine();

        if (audit is null)
        {
            report.AppendLine("No certification audit found. Run the certification suite before exporting.");
        }
        else
        {
            report.AppendLine($"PackageId: {audit.PackageId}");
            report.AppendLine($"OverallResult: {audit.OverallResult}");
            report.AppendLine($"StartedUtc: {audit.StartedUtc:O}");
            report.AppendLine($"CompletedUtc: {audit.CompletedUtc:O}");
            report.AppendLine();
            report.AppendLine("Steps:");
            foreach (var step in audit.Steps)
            {
                report.AppendLine(
                    $" - [{(step.Passed ? "PASS" : "FAIL")}] {step.Scenario} | {step.Endpoint} | HTTP {step.HttpStatusCode} | {step.DurationMs}ms");
                if (!string.IsNullOrWhiteSpace(step.Error))
                {
                    report.AppendLine($"     Error: {step.Error}");
                }
            }
        }

        report.AppendLine();
        report.AppendLine("Operator status log:");
        foreach (var line in (_auditStore as Compliance.MraCertificationAuditStore)?.StatusLines
                             ?? Array.Empty<string>())
        {
            report.AppendLine(line);
        }

        await File.WriteAllTextAsync(Path.Combine(staging, "TestExecutionReport.txt"), report.ToString(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static void CopySqlScripts(string staging)
    {
        var scriptsDir = Path.Combine(AppContext.BaseDirectory, "Scripts");
        var dest = Path.Combine(staging, "Scripts");
        if (!Directory.Exists(scriptsDir))
        {
            return;
        }

        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(scriptsDir, "*.sql"))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void CopySerilogSnippets(string staging)
    {
        var logsDir = Path.Combine(AppContext.BaseDirectory, "Logs");
        if (!Directory.Exists(logsDir))
        {
            return;
        }

        var dest = Path.Combine(staging, "RecentLogs");
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(logsDir, "*.log")
                     .OrderByDescending(File.GetLastWriteTimeUtc)
                     .Take(5))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
    }

    private async Task<string?> TryResolveTerminalIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            var audit = await _auditStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(audit?.TerminalId) &&
                !audit.TerminalId.Equals("UNASSIGNED", StringComparison.OrdinalIgnoreCase))
            {
                return audit.TerminalId;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string SanitizeFileToken(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}

public sealed record ComplianceExportResult(bool Success, string PackagePath, string Message);
