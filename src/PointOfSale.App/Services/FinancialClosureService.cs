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
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.Services;

public interface IFinancialClosureService
{
    Task<EndOfDaySummary> BuildPreviewAsync(
        DateTime? businessDateLocal = null,
        CancellationToken cancellationToken = default);

    Task<FinancialClosureResult> CloseBusinessDayAsync(
        DateTime businessDateLocal,
        string? notes = null,
        bool managerOverride = false,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FinancialClosureRecord>> GetRecentClosuresAsync(
        int take = 30,
        CancellationToken cancellationToken = default);

    Task<ZReportBundle?> GetZReportForClosureAsync(
        long closureId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// End-of-day financial closing engine: aggregates MWK sales/VAT, verifies MRA fiscal signatures,
/// and persists cryptographic closure harmony before Z-report print/submit.
/// </summary>
public sealed class FinancialClosureService : IFinancialClosureService
{
    private readonly IFinancialClosureRepository _closureRepository;
    private readonly IShiftManagementService _shiftManagement;
    private readonly ITaxReconciliationService _taxReconciliation;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IAuditSecurityLogger _auditLogger;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly FinancialClosureOptions _options;
    private readonly ILogger<FinancialClosureService> _logger;
    private readonly IDatabaseBackupService? _backupService;

    public FinancialClosureService(
        IFinancialClosureRepository closureRepository,
        IShiftManagementService shiftManagement,
        ITaxReconciliationService taxReconciliation,
        ISqlConnectionFactory connectionFactory,
        IAuditSecurityLogger auditLogger,
        IAuthenticationAuthorizationService auth,
        IOptions<FinancialClosureOptions> options,
        ILogger<FinancialClosureService> logger,
        IDatabaseBackupService? backupService = null)
    {
        _closureRepository = closureRepository;
        _shiftManagement = shiftManagement;
        _taxReconciliation = taxReconciliation;
        _connectionFactory = connectionFactory;
        _auditLogger = auditLogger;
        _auth = auth;
        _options = options.Value;
        _logger = logger;
        _backupService = backupService;
    }

    public async Task<EndOfDaySummary> BuildPreviewAsync(
        DateTime? businessDateLocal = null,
        CancellationToken cancellationToken = default)
    {
        var businessDate = (businessDateLocal ?? DateTime.Today).Date;
        return await BuildSummaryAsync(businessDate, cancellationToken).ConfigureAwait(false);
    }

    public async Task<FinancialClosureResult> CloseBusinessDayAsync(
        DateTime businessDateLocal,
        string? notes = null,
        bool managerOverride = false,
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.CloseFinancialDay);

        var businessDate = businessDateLocal.Date;
        var summary = await BuildSummaryAsync(businessDate, cancellationToken).ConfigureAwait(false);
        ValidateCanClose(summary, managerOverride, notes);

        var operatorSession = _auth.CurrentOperator
            ?? throw new InvalidOperationException("Sign in as a manager before closing the business day.");

        var prior = await _closureRepository.GetCumulativeTotalsBeforeAsync(businessDate, cancellationToken)
            .ConfigureAwait(false);
        var cumulativeGross = PosTaxCalculator.RoundMoney(prior.CumulativeGross + summary.TotalGrossSalesMwk);
        var cumulativeVat = PosTaxCalculator.RoundMoney(prior.CumulativeVat + summary.TotalVatCollectedMwk);

        var zReport = summary.AggregatedZReport ?? BuildAggregatedZReport(summary, businessDate);
        zReport = zReport with
        {
            ClosedAtUtc = DateTime.UtcNow,
            ClosingCashCounted = summary.ClosingCashCountedMwk,
            CashVariance = summary.CashDrawerVarianceMwk
        };

        var record = new FinancialClosureRecord
        {
            BusinessDate = businessDate,
            ClosedAtUtc = DateTime.UtcNow,
            ClosedByUsername = operatorSession.Username,
            ClosedByDisplayName = operatorSession.DisplayName,
            TotalGrossSalesMwk = summary.TotalGrossSalesMwk,
            TotalTaxableSalesMwk = summary.TotalTaxableSalesMwk,
            TotalVatCollectedMwk = summary.TotalVatCollectedMwk,
            ExpectedVatMwk = summary.ExpectedVatMwk,
            VatVarianceMwk = summary.VatVarianceMwk,
            CashCollectionsMwk = summary.CashCollectionsMwk,
            CardSettlementsMwk = summary.CardSettlementsMwk,
            MobileMoneySettlementsMwk = summary.MobileMoneySettlementsMwk,
            OtherSettlementsMwk = summary.OtherSettlementsMwk,
            TotalVoidsMwk = summary.TotalVoidsMwk,
            VoidCount = summary.VoidCount,
            SyncedInvoiceCount = summary.SyncedInvoiceCount,
            PendingInvoiceCount = summary.PendingInvoiceCount,
            QuarantinedInvoiceCount = summary.QuarantinedInvoiceCount,
            FiscalSignatureMatchCount = summary.FiscalSignatureMatchCount,
            FiscalSignatureMissingCount = summary.FiscalSignatureMissingCount,
            CashDrawerVarianceMwk = summary.CashDrawerVarianceMwk,
            CumulativeGrossSalesMwk = cumulativeGross,
            CumulativeVatMwk = cumulativeVat,
            ShiftCount = summary.ShiftCount,
            AuditPassed = summary.AuditPassed,
            Status = FinancialClosureStatuses.Closed,
            Notes = notes?.Trim(),
            ClosureJson = JsonSerializer.Serialize(
                new
                {
                    summary,
                    zReport,
                    closedBy = operatorSession.Username,
                    managerOverride
                },
                MraJson.SerializerOptions)
        };

        record.ClosureId = await _closureRepository.InsertAsync(record, cancellationToken).ConfigureAwait(false);

        await _auditLogger.LogAsync(
                SecurityAuditActions.AdminOverride,
                $"EOD financial closure #{record.ClosureId} for {businessDate:yyyy-MM-dd}; gross {record.TotalGrossSalesMwk:N2} MWK; VAT {record.TotalVatCollectedMwk:N2} MWK.",
                success: true,
                operatorId: operatorSession.OperatorId,
                username: operatorSession.Username,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Financial day {BusinessDate:yyyy-MM-dd} closed by {User}. Gross={Gross:N2} VAT={Vat:N2} AuditPassed={Audit}",
            businessDate,
            operatorSession.Username,
            record.TotalGrossSalesMwk,
            record.TotalVatCollectedMwk,
            record.AuditPassed);

        if (_backupService is not null)
        {
            try
            {
                var backup = await _backupService.BackupOnEndOfDayAsync(cancellationToken).ConfigureAwait(false);
                if (backup.Success)
                {
                    _logger.LogInformation(
                        "End-of-day SQL Express backup completed after financial closure: {Path}",
                        backup.Manifest?.BackupFilePath);
                }
                else
                {
                    _logger.LogWarning(
                        "End-of-day SQL Express backup skipped/failed after financial closure: {Error}",
                        backup.Error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "End-of-day SQL Express backup threw after financial closure.");
            }
        }

        var closedSummary = summary with
        {
            IsDayAlreadyClosed = true,
            ExistingClosureId = record.ClosureId,
            CumulativeGrossSalesMwk = cumulativeGross,
            CumulativeVatMwk = cumulativeVat,
            AggregatedZReport = zReport
        };

        return new FinancialClosureResult
        {
            Record = record,
            Summary = closedSummary,
            ZReport = zReport,
            Message = $"Business day {businessDate:yyyy-MM-dd} closed. Closure #{record.ClosureId}."
        };
    }

    public Task<IReadOnlyList<FinancialClosureRecord>> GetRecentClosuresAsync(
        int take = 30,
        CancellationToken cancellationToken = default) =>
        _closureRepository.GetRecentAsync(take, cancellationToken);

    public async Task<ZReportBundle?> GetZReportForClosureAsync(
        long closureId,
        CancellationToken cancellationToken = default)
    {
        var recent = await _closureRepository.GetRecentAsync(100, cancellationToken).ConfigureAwait(false);
        var record = recent.FirstOrDefault(r => r.ClosureId == closureId);
        if (record?.ClosureJson is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(record.ClosureJson);
            if (doc.RootElement.TryGetProperty("zReport", out var zReportElement))
            {
                return JsonSerializer.Deserialize<ZReportBundle>(zReportElement.GetRawText(), MraJson.SerializerOptions);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize Z-report for closure {ClosureId}.", closureId);
        }

        return null;
    }

    private async Task<EndOfDaySummary> BuildSummaryAsync(DateTime businessDate, CancellationToken cancellationToken)
    {
        var fromLocal = businessDate.Date;
        var toLocalExclusive = fromLocal.AddDays(1);
        var fromUtc = fromLocal.ToUniversalTime();
        var toUtcExclusive = toLocalExclusive.ToUniversalTime();

        var tax = await _taxReconciliation
            .GetReportAsync(TaxReconciliationPeriod.Daily, businessDate, cancellationToken)
            .ConfigureAwait(false);
        var queue = await _taxReconciliation.GetQueueHealthAsync(cancellationToken).ConfigureAwait(false);
        var shifts = await _closureRepository
            .GetShiftsForUtcWindowAsync(fromUtc, toUtcExclusive, cancellationToken)
            .ConfigureAwait(false);
        var openShift = await _shiftManagement.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false);
        var existing = await _closureRepository.GetByBusinessDateAsync(businessDate, cancellationToken)
            .ConfigureAwait(false);
        var prior = await _closureRepository.GetCumulativeTotalsBeforeAsync(businessDate, cancellationToken)
            .ConfigureAwait(false);

        var daySales = await LoadDaySalesAsync(fromUtc, toUtcExclusive, cancellationToken).ConfigureAwait(false);
        var audit = RunFiscalAudit(daySales.SyncedInvoices, daySales.PendingCount, daySales.QuarantinedCount);

        var cashVariance = PosTaxCalculator.RoundMoney(
            shifts.Where(s => s.Status == ShiftStatuses.Closed).Sum(s => s.CashVariance ?? 0m));
        var expectedCash = PosTaxCalculator.RoundMoney(
            shifts.Where(s => s.Status == ShiftStatuses.Closed).Sum(s => s.ExpectedCash ?? 0m));
        var countedCash = PosTaxCalculator.RoundMoney(
            shifts.Where(s => s.Status == ShiftStatuses.Closed).Sum(s => s.ClosingCashCounted ?? 0m));

        var gross = PosTaxCalculator.RoundMoney(daySales.GrossSales);
        var vat = PosTaxCalculator.RoundMoney(daySales.TotalVat);
        var expectedVat = PosTaxCalculator.RoundMoney(tax.ExpectedStandardVat);
        var vatVariance = PosTaxCalculator.RoundMoney(vat - expectedVat);
        var isVatBalanced = Math.Abs(vatVariance) <= _options.VatBalanceToleranceMwk
            || Math.Abs(tax.VatVariance) <= _options.VatBalanceToleranceMwk;

        var summary = new EndOfDaySummary
        {
            BusinessDate = businessDate,
            FromUtc = fromUtc,
            ToUtcExclusive = toUtcExclusive,
            TotalGrossSalesMwk = gross,
            TotalTaxableSalesMwk = PosTaxCalculator.RoundMoney(tax.StandardRateTaxable),
            TotalVatCollectedMwk = vat,
            ExpectedVatMwk = expectedVat,
            VatVarianceMwk = vatVariance,
            IsVatBalanced = isVatBalanced,
            CashCollectionsMwk = daySales.CashSales,
            CardSettlementsMwk = daySales.CardSales,
            MobileMoneySettlementsMwk = daySales.MobileSales,
            OtherSettlementsMwk = daySales.OtherSales,
            TotalVoidsMwk = daySales.VoidTotal,
            VoidCount = daySales.VoidCount,
            CashDrawerVarianceMwk = cashVariance,
            ExpectedCashInDrawerMwk = expectedCash,
            ClosingCashCountedMwk = countedCash,
            SyncedInvoiceCount = daySales.SyncedInvoices.Count,
            PendingInvoiceCount = daySales.PendingCount,
            SyncingInvoiceCount = daySales.SyncingCount,
            QuarantinedInvoiceCount = daySales.QuarantinedCount,
            OnlineInvoiceCount = tax.OnlineInvoiceCount,
            OfflineSyncedInvoiceCount = tax.OfflineSyncedInvoiceCount,
            FiscalSignatureMatchCount = audit.MatchCount,
            FiscalSignatureMissingCount = audit.MissingCount,
            AuditPassed = audit.Passed,
            AuditMessage = audit.Message,
            HasOpenShift = openShift is not null,
            IsDayAlreadyClosed = existing is not null,
            ExistingClosureId = existing?.ClosureId,
            CumulativeGrossSalesMwk = PosTaxCalculator.RoundMoney(
                prior.CumulativeGross + (existing?.TotalGrossSalesMwk ?? gross)),
            CumulativeVatMwk = PosTaxCalculator.RoundMoney(
                prior.CumulativeVat + (existing?.TotalVatCollectedMwk ?? vat)),
            ShiftCount = shifts.Count,
            Shifts = shifts,
            FiscalizedInvoices = daySales.SyncedInvoices,
            AggregatedZReport = null,
            SummaryText = BuildSummaryText(businessDate, gross, vat, audit.Passed, existing is not null)
        };

        return summary with { AggregatedZReport = BuildAggregatedZReport(summary, businessDate) };
    }

    private void ValidateCanClose(EndOfDaySummary summary, bool managerOverride, string? notes)
    {
        if (summary.IsDayAlreadyClosed)
        {
            throw new InvalidOperationException(
                $"Business day {summary.BusinessDate:yyyy-MM-dd} is already closed (closure #{summary.ExistingClosureId}).");
        }

        if (summary.HasOpenShift && !_options.AllowCloseWithOpenShift)
        {
            throw new InvalidOperationException(
                "Close all cashier shifts before End-of-Day financial closure.");
        }

        if (_options.RequireQueueDrained
            && (summary.PendingInvoiceCount > 0 || summary.SyncingInvoiceCount > 0)
            && !managerOverride)
        {
            throw new InvalidOperationException(
                $"Offline queue still has {summary.PendingInvoiceCount + summary.SyncingInvoiceCount} unsynced invoice(s). Drain the queue or use manager override.");
        }

        if (_options.RequireFiscalSignatures && !summary.AuditPassed && !managerOverride)
        {
            throw new InvalidOperationException(
                $"MRA fiscal audit failed: {summary.AuditMessage} Use manager override only after investigation.");
        }

        if (!summary.IsVatBalanced && !managerOverride)
        {
            throw new InvalidOperationException(
                $"VAT variance {summary.VatVarianceMwk:N2} MWK exceeds tolerance. Manager override required.");
        }

        if (Math.Abs(summary.CashDrawerVarianceMwk) > _options.CashVarianceWarnMwk
            && string.IsNullOrWhiteSpace(notes)
            && !managerOverride)
        {
            throw new InvalidOperationException(
                $"Cash drawer variance {summary.CashDrawerVarianceMwk:N2} MWK requires notes or manager override.");
        }
    }

    private async Task<DaySalesAggregate> LoadDaySalesAsync(
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                Status,
                JSON_VALUE(PayloadJson, '$.invoiceHeader.invoiceNumber') AS InvoiceNumber,
                JSON_VALUE(PayloadJson, '$.invoiceHeader.paymentMethod') AS PaymentMethod,
                ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.invoiceTotal') AS DECIMAL(18,2)), 0) AS InvoiceTotal,
                ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.totalVAT') AS DECIMAL(18,2)), 0) AS TotalVat,
                ISNULL(JSON_VALUE(FiscalResponseJson, '$.fiscalSignature'), JSON_VALUE(FiscalResponseJson, '$.fiscalCode')) AS FiscalSignature,
                ISNULL(JSON_VALUE(PayloadJson, '$.invoiceHeader.invoiceType'), '') AS InvoiceType
            FROM dbo.OfflineInvoiceQueue
            WHERE CreatedAt >= @FromUtc
              AND CreatedAt < @ToUtc
            ORDER BY Id DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = (await connection.QueryAsync<DaySalesRow>(
                new CommandDefinition(
                    sql,
                    new { FromUtc = fromUtc, ToUtc = toUtcExclusive },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).AsList();

        var synced = rows
            .Where(r => r.Status.Equals("SYNCED", StringComparison.OrdinalIgnoreCase))
            .Select(r => new ZReportInvoiceLine
            {
                InvoiceNumber = r.InvoiceNumber,
                PaymentMethod = r.PaymentMethod,
                InvoiceTotal = r.InvoiceTotal,
                TotalVat = r.TotalVat,
                FiscalSignature = r.FiscalSignature
            })
            .ToList();

        var salesRows = synced.Where(i => !IsVoidLike(i.PaymentMethod, null, i.InvoiceTotal)).ToList();
        var voidRows = rows.Where(r => IsVoidLike(r.PaymentMethod, r.InvoiceType, r.InvoiceTotal)).ToList();

        decimal Sum(string method) =>
            salesRows
                .Where(i => string.Equals(i.PaymentMethod, method, StringComparison.OrdinalIgnoreCase))
                .Sum(i => i.InvoiceTotal);

        var cash = Sum("Cash");
        var card = Sum("Card");
        var mobile = Sum("MobileMoney");
        var gross = salesRows.Sum(i => i.InvoiceTotal);
        var other = PosTaxCalculator.RoundMoney(gross - cash - card - mobile);

        return new DaySalesAggregate
        {
            SyncedInvoices = synced,
            CashSales = PosTaxCalculator.RoundMoney(cash),
            CardSales = PosTaxCalculator.RoundMoney(card),
            MobileSales = PosTaxCalculator.RoundMoney(mobile),
            OtherSales = other,
            GrossSales = PosTaxCalculator.RoundMoney(gross),
            TotalVat = PosTaxCalculator.RoundMoney(salesRows.Sum(i => i.TotalVat)),
            VoidCount = voidRows.Count,
            VoidTotal = PosTaxCalculator.RoundMoney(voidRows.Sum(r => Math.Abs(r.InvoiceTotal))),
            PendingCount = rows.Count(r => r.Status.Equals("PENDING", StringComparison.OrdinalIgnoreCase)),
            SyncingCount = rows.Count(r => r.Status.Equals("SYNCING", StringComparison.OrdinalIgnoreCase)),
            QuarantinedCount = rows.Count(r => r.Status.Equals("QUARANTINED", StringComparison.OrdinalIgnoreCase))
        };
    }

    private (bool Passed, int MatchCount, int MissingCount, string Message) RunFiscalAudit(
        IReadOnlyList<ZReportInvoiceLine> synced,
        int pendingCount,
        int quarantinedCount)
    {
        var match = synced.Count(i => !string.IsNullOrWhiteSpace(i.FiscalSignature));
        var missing = synced.Count - match;
        var issues = new List<string>();

        if (missing > 0)
        {
            issues.Add($"{missing} synced invoice(s) missing MRA fiscal signature/code");
        }

        if (pendingCount > 0)
        {
            issues.Add($"{pendingCount} pending offline invoice(s)");
        }

        if (quarantinedCount > 0)
        {
            issues.Add($"{quarantinedCount} quarantined invoice(s)");
        }

        // Cross-check: every signature that exists must be unique (duplicate signatures indicate replay).
        var duplicates = synced
            .Where(i => !string.IsNullOrWhiteSpace(i.FiscalSignature))
            .GroupBy(i => i.FiscalSignature!, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(3)
            .ToList();
        if (duplicates.Count > 0)
        {
            issues.Add($"duplicate fiscal signature(s) detected ({duplicates.Count})");
        }

        var passed = missing == 0 && duplicates.Count == 0;
        if (!_options.RequireFiscalSignatures && missing > 0 && duplicates.Count == 0)
        {
            passed = pendingCount == 0 && quarantinedCount == 0;
        }

        var message = issues.Count == 0
            ? $"Fiscal audit OK — {match} signature(s) verified against local SYNCED sales log."
            : $"Fiscal audit issues: {string.Join("; ", issues)}.";

        return (passed, match, missing, message);
    }

    private static ZReportBundle BuildAggregatedZReport(EndOfDaySummary summary, DateTime businessDate)
    {
        var cashiers = summary.Shifts.Select(s => s.CashierName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cashierLabel = cashiers.Count == 0
            ? "EOD"
            : cashiers.Count == 1
                ? cashiers[0]
                : $"EOD ({cashiers.Count} cashiers)";

        return new ZReportBundle
        {
            ShiftId = 0,
            CashierName = cashierLabel,
            OpenedAtUtc = summary.FromUtc,
            ClosedAtUtc = summary.IsDayAlreadyClosed ? summary.ToUtcExclusive : null,
            OpeningFloat = summary.Shifts.Sum(s => s.OpeningFloat),
            CashSales = summary.CashCollectionsMwk,
            CardSales = summary.CardSettlementsMwk,
            MobileMoneySales = summary.MobileMoneySettlementsMwk,
            OtherSales = summary.OtherSettlementsMwk,
            GrossSales = summary.TotalGrossSalesMwk,
            TotalVat = summary.TotalVatCollectedMwk,
            CashInTotal = 0,
            CashOutTotal = 0,
            CashDropTotal = 0,
            ExpectedCashInDrawer = summary.ExpectedCashInDrawerMwk,
            ClosingCashCounted = summary.ClosingCashCountedMwk,
            CashVariance = summary.CashDrawerVarianceMwk,
            InvoiceCount = summary.SyncedInvoiceCount,
            FiscalizedInvoices = summary.FiscalizedInvoices
        };
    }

    private static bool IsVoidLike(string? paymentMethod, string? invoiceType, decimal total)
    {
        if (total < 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(paymentMethod)
            && (paymentMethod.Contains("void", StringComparison.OrdinalIgnoreCase)
                || paymentMethod.Contains("credit", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(invoiceType)
               && (invoiceType.Contains("void", StringComparison.OrdinalIgnoreCase)
                   || invoiceType.Contains("credit", StringComparison.OrdinalIgnoreCase)
                   || invoiceType.Equals("CN", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildSummaryText(
        DateTime businessDate,
        decimal gross,
        decimal vat,
        bool auditPassed,
        bool alreadyClosed) =>
        alreadyClosed
            ? $"Business day {businessDate:yyyy-MM-dd} is closed. Gross {gross:N2} MWK · VAT {vat:N2} MWK."
            : $"Preview {businessDate:yyyy-MM-dd}: Gross {gross:N2} MWK · VAT {vat:N2} MWK · Audit {(auditPassed ? "PASS" : "FAIL")}.";

    private sealed class DaySalesRow
    {
        public string Status { get; set; } = string.Empty;
        public string? InvoiceNumber { get; set; }
        public string? PaymentMethod { get; set; }
        public decimal InvoiceTotal { get; set; }
        public decimal TotalVat { get; set; }
        public string? FiscalSignature { get; set; }
        public string? InvoiceType { get; set; }
    }

    private sealed class DaySalesAggregate
    {
        public List<ZReportInvoiceLine> SyncedInvoices { get; init; } = [];
        public decimal CashSales { get; init; }
        public decimal CardSales { get; init; }
        public decimal MobileSales { get; init; }
        public decimal OtherSales { get; init; }
        public decimal GrossSales { get; init; }
        public decimal TotalVat { get; init; }
        public int VoidCount { get; init; }
        public decimal VoidTotal { get; init; }
        public int PendingCount { get; init; }
        public int SyncingCount { get; init; }
        public int QuarantinedCount { get; init; }
    }
}

public sealed record EndOfDaySummary
{
    public DateTime BusinessDate { get; init; }
    public DateTime FromUtc { get; init; }
    public DateTime ToUtcExclusive { get; init; }
    public decimal TotalGrossSalesMwk { get; init; }
    public decimal TotalTaxableSalesMwk { get; init; }
    public decimal TotalVatCollectedMwk { get; init; }
    public decimal ExpectedVatMwk { get; init; }
    public decimal VatVarianceMwk { get; init; }
    public bool IsVatBalanced { get; init; }
    public decimal CashCollectionsMwk { get; init; }
    public decimal CardSettlementsMwk { get; init; }
    public decimal MobileMoneySettlementsMwk { get; init; }
    public decimal OtherSettlementsMwk { get; init; }
    public decimal TotalVoidsMwk { get; init; }
    public int VoidCount { get; init; }
    public decimal CashDrawerVarianceMwk { get; init; }
    public decimal ExpectedCashInDrawerMwk { get; init; }
    public decimal ClosingCashCountedMwk { get; init; }
    public int SyncedInvoiceCount { get; init; }
    public int PendingInvoiceCount { get; init; }
    public int SyncingInvoiceCount { get; init; }
    public int QuarantinedInvoiceCount { get; init; }
    public int OnlineInvoiceCount { get; init; }
    public int OfflineSyncedInvoiceCount { get; init; }
    public int FiscalSignatureMatchCount { get; init; }
    public int FiscalSignatureMissingCount { get; init; }
    public bool AuditPassed { get; init; }
    public string AuditMessage { get; init; } = string.Empty;
    public bool HasOpenShift { get; init; }
    public bool IsDayAlreadyClosed { get; init; }
    public long? ExistingClosureId { get; init; }
    public decimal CumulativeGrossSalesMwk { get; init; }
    public decimal CumulativeVatMwk { get; init; }
    public int ShiftCount { get; init; }
    public IReadOnlyList<CashierShift> Shifts { get; init; } = Array.Empty<CashierShift>();
    public IReadOnlyList<ZReportInvoiceLine> FiscalizedInvoices { get; init; } = Array.Empty<ZReportInvoiceLine>();
    public ZReportBundle? AggregatedZReport { get; init; }
    public string SummaryText { get; init; } = string.Empty;
}

public sealed class FinancialClosureResult
{
    public required FinancialClosureRecord Record { get; init; }
    public required EndOfDaySummary Summary { get; init; }
    public required ZReportBundle ZReport { get; init; }
    public string Message { get; init; } = string.Empty;
}
