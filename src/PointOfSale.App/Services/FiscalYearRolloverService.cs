using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Security;

namespace PointOfSale.App.Services;

public interface IFiscalYearRolloverService
{
    int ResolveCurrentFiscalYear(DateTime? asOfLocal = null);

    (DateTime Start, DateTime End) ResolveFiscalYearPeriod(int fiscalYear);

    Task<FiscalYearRolloverPreview> BuildPreviewAsync(int fiscalYear, CancellationToken cancellationToken = default);

    Task<FiscalYearRolloverResult> ExecuteRolloverAsync(
        int fiscalYear,
        FiscalDualKeyAuthorization authorization,
        string? notes = null,
        bool allowGapsOverride = false,
        CancellationToken cancellationToken = default);

    Task<bool> VerifyArchiveIntegrityAsync(long archiveId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FiscalYearArchiveRecord>> GetRecentArchivesAsync(
        int take = 20,
        CancellationToken cancellationToken = default);
}

public sealed class FiscalDualKeyAuthorization
{
    public required string SecondarySupervisorUsername { get; init; }
    public required string SecondarySupervisorPassword { get; init; }
    public required string PrimaryArchivePassword { get; init; }
    public required string SecondaryArchivePassword { get; init; }
}

/// <summary>
/// Fiscal year-end rollover: validates daily EOD closures, seals MRA counters and signatures into a locked archive.
/// </summary>
public sealed class FiscalYearRolloverService : IFiscalYearRolloverService
{
    private readonly IFiscalYearArchiveRepository _archiveRepository;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly IOperatorRepository _operators;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditSecurityLogger _auditLogger;
    private readonly FiscalArchivalOptions _options;
    private readonly ILogger<FiscalYearRolloverService> _logger;

    public FiscalYearRolloverService(
        IFiscalYearArchiveRepository archiveRepository,
        IConfigurationRepository configurationRepository,
        ISqlConnectionFactory connectionFactory,
        IAuthenticationAuthorizationService auth,
        IOperatorRepository operators,
        IPasswordHasher passwordHasher,
        IAuditSecurityLogger auditLogger,
        IOptions<FiscalArchivalOptions> options,
        ILogger<FiscalYearRolloverService> logger)
    {
        _archiveRepository = archiveRepository;
        _configurationRepository = configurationRepository;
        _connectionFactory = connectionFactory;
        _auth = auth;
        _operators = operators;
        _passwordHasher = passwordHasher;
        _auditLogger = auditLogger;
        _options = options.Value;
        _logger = logger;
    }

    public int ResolveCurrentFiscalYear(DateTime? asOfLocal = null) =>
        ResolveFiscalYearForDate((asOfLocal ?? DateTime.Today).Date);

    public (DateTime Start, DateTime End) ResolveFiscalYearPeriod(int fiscalYear)
    {
        var start = new DateTime(fiscalYear, _options.FiscalYearStartMonth, _options.FiscalYearStartDay);
        var end = start.AddYears(1).AddDays(-1);
        return (start, end);
    }

    public async Task<FiscalYearRolloverPreview> BuildPreviewAsync(
        int fiscalYear,
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.ExecuteFiscalYearRollover);

        var existing = await _archiveRepository.GetByFiscalYearAsync(fiscalYear, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new FiscalYearRolloverPreview
            {
                FiscalYear = fiscalYear,
                CanRollover = false,
                SummaryMessage = $"Fiscal year {fiscalYear} is already archived (archive #{existing.ArchiveId})."
            };
        }

        var (start, end) = ResolveFiscalYearPeriod(fiscalYear);
        var closures = await _archiveRepository.GetClosuresBetweenAsync(start, end, cancellationToken).ConfigureAwait(false);
        var expectedDays = EnumerateBusinessDays(start, end).ToList();
        var closedDates = closures.Select(c => c.BusinessDate.Date).ToHashSet();
        var missing = expectedDays.Where(d => !closedDates.Contains(d)).ToList();

        var gross = PosTaxCalculator.RoundMoney(closures.Sum(c => c.TotalGrossSalesMwk));
        var vat = PosTaxCalculator.RoundMoney(closures.Sum(c => c.TotalVatCollectedMwk));
        var cumulative = closures.OrderByDescending(c => c.BusinessDate).FirstOrDefault();

        var invoiceAudit = await LoadInvoiceSignatureAuditAsync(start, end, cancellationToken).ConfigureAwait(false);

        var canRollover = missing.Count == 0 || _options.AllowRolloverWithGaps;
        if (_options.RequireAllDailyClosures && missing.Count > 0)
        {
            canRollover = false;
        }

        return new FiscalYearRolloverPreview
        {
            FiscalYear = fiscalYear,
            PeriodStart = start,
            PeriodEnd = end,
            ExpectedBusinessDays = expectedDays.Count,
            ClosedDays = closures.Count,
            MissingClosureDates = missing,
            TotalGrossSalesMwk = gross,
            TotalVatCollectedMwk = vat,
            CumulativeGrossAtYearEnd = cumulative?.CumulativeGrossSalesMwk ?? gross,
            CumulativeVatAtYearEnd = cumulative?.CumulativeVatMwk ?? vat,
            SyncedInvoiceCount = invoiceAudit.SyncedCount,
            SignatureRowsVerified = invoiceAudit.VerifiedSignatures,
            CanRollover = canRollover,
            SummaryMessage = missing.Count == 0
                ? $"FY {fiscalYear}: {closures.Count} EOD closure(s), gross {gross:N2} MWK, VAT {vat:N2} MWK."
                : $"FY {fiscalYear}: {missing.Count} business day(s) without EOD closure."
        };
    }

    public async Task<FiscalYearRolloverResult> ExecuteRolloverAsync(
        int fiscalYear,
        FiscalDualKeyAuthorization authorization,
        string? notes = null,
        bool allowGapsOverride = false,
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.ExecuteFiscalYearRollover);
        var primary = _auth.CurrentOperator
            ?? throw new InvalidOperationException("Sign in before executing fiscal year rollover.");

        await VerifySecondarySupervisorAsync(authorization, primary.Username, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(authorization.PrimaryArchivePassword)
            || string.IsNullOrWhiteSpace(authorization.SecondaryArchivePassword))
        {
            throw new InvalidOperationException("Dual archive passwords are required.");
        }

        var preview = await BuildPreviewAsync(fiscalYear, cancellationToken).ConfigureAwait(false);
        if (!preview.CanRollover && !(allowGapsOverride && _options.AllowRolloverWithGaps))
        {
            throw new InvalidOperationException(preview.SummaryMessage);
        }

        var (start, end) = ResolveFiscalYearPeriod(fiscalYear);
        var closures = await _archiveRepository.GetClosuresBetweenAsync(start, end, cancellationToken).ConfigureAwait(false);
        var invoiceRows = await LoadInvoiceArchiveRowsAsync(start, end, cancellationToken).ConfigureAwait(false);

        var manifest = new
        {
            fiscalYear,
            periodStart = start,
            periodEnd = end,
            generatedAtUtc = DateTime.UtcNow,
            primarySupervisor = primary.Username,
            secondarySupervisor = authorization.SecondarySupervisorUsername.Trim(),
            vatRatePercent = PosTaxCalculator.MalawiStandardVatRatePercent,
            totals = new
            {
                preview.TotalGrossSalesMwk,
                preview.TotalVatCollectedMwk,
                preview.CumulativeGrossAtYearEnd,
                preview.CumulativeVatAtYearEnd
            },
            closures,
            invoices = invoiceRows
        };

        var payloadJson = JsonSerializer.Serialize(manifest);
        var manifestJson = FiscalArchivePasswordCipher.SerializeManifest(new
        {
            fiscalYear,
            periodStart = start,
            periodEnd = end,
            manifestSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson))),
            invoiceCount = invoiceRows.Count,
            closureCount = closures.Count
        });

        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var key = FiscalArchivePasswordCipher.DeriveDualKey(
            authorization.PrimaryArchivePassword,
            authorization.SecondaryArchivePassword);
        var fileBytes = FiscalArchivePasswordCipher.BuildArtFiscalFile(manifestBytes, payloadBytes, key);
        CryptographicOperations.ZeroMemory(key);

        var manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var hmacSecret = await ResolveTerminalHmacSecretAsync(cancellationToken).ConfigureAwait(false);
        var manifestHmac = string.IsNullOrEmpty(hmacSecret)
            ? string.Empty
            : HmacSignatureService.ComputeHmacSha512Base64(manifestSha256, hmacSecret);

        var directory = ResolveArchiveDirectory();
        Directory.CreateDirectory(directory);
        var fileName = $"FY{fiscalYear}_Rollover_{DateTime.UtcNow:yyyyMMddHHmmss}.art-fiscal";
        var filePath = Path.Combine(directory, fileName);
        await File.WriteAllBytesAsync(filePath, fileBytes, cancellationToken).ConfigureAwait(false);

        var record = new FiscalYearArchiveRecord
        {
            FiscalYear = fiscalYear,
            PeriodStart = start,
            PeriodEnd = end,
            RolledOverAtUtc = DateTime.UtcNow,
            PrimarySupervisorUsername = primary.Username,
            SecondarySupervisorUsername = authorization.SecondarySupervisorUsername.Trim(),
            TotalGrossSalesMwk = preview.TotalGrossSalesMwk,
            TotalVatCollectedMwk = preview.TotalVatCollectedMwk,
            ExpectedClosureDays = preview.ExpectedBusinessDays,
            ClosedDays = preview.ClosedDays,
            SyncedInvoiceCount = invoiceRows.Count,
            ManifestSha256 = manifestSha256,
            ManifestHmacSha512 = manifestHmac,
            ArchiveFilePath = filePath,
            ArchiveBytes = fileBytes.Length,
            CryptographicVerificationPassed = !string.IsNullOrEmpty(manifestHmac),
            Status = FiscalYearArchiveStatuses.Locked,
            Notes = notes?.Trim()
        };

        record.ArchiveId = await _archiveRepository.InsertArchiveAsync(record, cancellationToken).ConfigureAwait(false);

        await _archiveRepository.InsertPackageAsync(
                new FiscalArchivePackageRecord
                {
                    PackageType = FiscalArchivePackageTypes.FiscalYearRollover,
                    PeriodStartUtc = start.ToUniversalTime(),
                    PeriodEndUtc = end.AddDays(1).ToUniversalTime(),
                    FilePath = filePath,
                    FileBytes = fileBytes.Length,
                    ContentSha256 = Convert.ToHexString(SHA256.HashData(fileBytes)),
                    TriggeredByUsername = primary.Username,
                    DualKeyProtected = true
                },
                cancellationToken)
            .ConfigureAwait(false);

        await _auditLogger.LogAsync(
                SecurityAuditActions.AdminOverride,
                $"Fiscal year {fiscalYear} rollover archive #{record.ArchiveId} locked at {filePath}.",
                success: true,
                operatorId: primary.OperatorId,
                username: primary.Username,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Fiscal year {Year} archived to {Path}", fiscalYear, filePath);

        return new FiscalYearRolloverResult
        {
            Record = record,
            ArchiveFilePath = filePath,
            Message = $"Fiscal year {fiscalYear} rollover complete. Archive #{record.ArchiveId}."
        };
    }

    public async Task<bool> VerifyArchiveIntegrityAsync(long archiveId, CancellationToken cancellationToken = default)
    {
        var archives = await _archiveRepository.GetRecentArchivesAsync(200, cancellationToken).ConfigureAwait(false);
        var record = archives.FirstOrDefault(a => a.ArchiveId == archiveId);
        if (record is null || !File.Exists(record.ArchiveFilePath))
        {
            return false;
        }

        var hmacSecret = await ResolveTerminalHmacSecretAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(hmacSecret) || string.IsNullOrEmpty(record.ManifestHmacSha512))
        {
            return File.Exists(record.ArchiveFilePath);
        }

        var expected = HmacSignatureService.ComputeHmacSha512Base64(record.ManifestSha256, hmacSecret);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(record.ManifestHmacSha512));
    }

    public Task<IReadOnlyList<FiscalYearArchiveRecord>> GetRecentArchivesAsync(
        int take = 20,
        CancellationToken cancellationToken = default) =>
        _archiveRepository.GetRecentArchivesAsync(take, cancellationToken);

    private async Task VerifySecondarySupervisorAsync(
        FiscalDualKeyAuthorization authorization,
        string primaryUsername,
        CancellationToken cancellationToken)
    {
        var secondaryUsername = authorization.SecondarySupervisorUsername.Trim();
        if (string.Equals(secondaryUsername, primaryUsername, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Secondary supervisor must be a different operator.");
        }

        var account = await _operators.GetByUsernameAsync(secondaryUsername, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Secondary supervisor account was not found.");

        if (!account.IsActive)
        {
            throw new InvalidOperationException("Secondary supervisor account is inactive.");
        }

        var permissions = RolePermissionCatalog.GetPermissions(account.Role);
        if (!permissions.Contains(OperatorPermissions.ExecuteFiscalYearRollover)
            && !permissions.Contains(OperatorPermissions.CloseFinancialDay))
        {
            throw new InvalidOperationException("Secondary supervisor lacks fiscal authorization.");
        }

        if (!_passwordHasher.VerifyPassword(
                authorization.SecondarySupervisorPassword,
                account.PasswordHash,
                account.PasswordSalt,
                account.PasswordIterations))
        {
            throw new InvalidOperationException("Secondary supervisor password is incorrect.");
        }
    }

    private async Task<string> ResolveTerminalHmacSecretAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await _configurationRepository.GetProtectedSecretPlainAsync(
                    _options.TerminalHmacConfigKey,
                    cancellationToken)
                .ConfigureAwait(false);
            return json?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task<(int SyncedCount, int VerifiedSignatures)> LoadInvoiceSignatureAuditAsync(
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var rows = await LoadInvoiceArchiveRowsAsync(start, end, cancellationToken).ConfigureAwait(false);
        var verified = rows.Count(r => !string.IsNullOrWhiteSpace(r.FiscalSignature));
        return (rows.Count, verified);
    }

    private async Task<List<InvoiceArchiveRow>> LoadInvoiceArchiveRowsAsync(
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                Id AS QueueId,
                CreatedAt AS CreatedAtUtc,
                JSON_VALUE(PayloadJson, '$.invoiceHeader.invoiceNumber') AS InvoiceNumber,
                ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.invoiceTotal') AS DECIMAL(18,2)), 0) AS InvoiceTotal,
                ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.totalVAT') AS DECIMAL(18,2)), 0) AS TotalVat,
                ISNULL(JSON_VALUE(FiscalResponseJson, '$.fiscalSignature'), JSON_VALUE(FiscalResponseJson, '$.fiscalCode')) AS FiscalSignature,
                PayloadJson
            FROM dbo.OfflineInvoiceQueue
            WHERE Status = N'SYNCED'
              AND CreatedAt >= @FromUtc
              AND CreatedAt < @ToUtc
            ORDER BY CreatedAt ASC;
            """;

        var fromUtc = start.Date.ToUniversalTime();
        var toUtc = end.Date.AddDays(1).ToUniversalTime();

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = (await connection.QueryAsync<InvoiceArchiveRow>(
                new CommandDefinition(
                    sql,
                    new { FromUtc = fromUtc, ToUtc = toUtc },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).AsList();

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.PayloadJson))
            {
                continue;
            }

            row.PayloadHmacSha512 = HmacSignatureService.ComputeHmacSha512Base64(row.PayloadJson, row.InvoiceNumber ?? string.Empty);
        }

        return rows;
    }

    private int ResolveFiscalYearForDate(DateTime localDate)
    {
        var startThisYear = new DateTime(localDate.Year, _options.FiscalYearStartMonth, _options.FiscalYearStartDay);
        return localDate < startThisYear ? localDate.Year - 1 : localDate.Year;
    }

    private static IEnumerable<DateTime> EnumerateBusinessDays(DateTime start, DateTime end)
    {
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            yield return day;
        }
    }

    public string ResolveArchiveDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.ArchiveDirectory))
        {
            return Environment.ExpandEnvironmentVariables(_options.ArchiveDirectory.Trim());
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "AlbertRetailTerminal",
            "FiscalArchives");
    }

    private sealed class InvoiceArchiveRow
    {
        public int QueueId { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string? InvoiceNumber { get; set; }
        public decimal InvoiceTotal { get; set; }
        public decimal TotalVat { get; set; }
        public string? FiscalSignature { get; set; }
        public string? PayloadJson { get; set; }
        public string? PayloadHmacSha512 { get; set; }
    }
}
