using Dapper;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.App.Services;

public interface ITaxReconciliationService
{
    Task<TaxReconciliationReport> GetReportAsync(
        TaxReconciliationPeriod period,
        DateTime? asOfLocalDate = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HourlySalesPoint>> GetHourlySalesVelocityAsync(
        DateTime localDay,
        CancellationToken cancellationToken = default);

    Task<QueueHealthSnapshot> GetQueueHealthAsync(CancellationToken cancellationToken = default);
}

public enum TaxReconciliationPeriod
{
    Daily,
    Weekly,
    Monthly
}

public sealed class TaxReconciliationService : ITaxReconciliationService
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public TaxReconciliationService(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<TaxReconciliationReport> GetReportAsync(
        TaxReconciliationPeriod period,
        DateTime? asOfLocalDate = null,
        CancellationToken cancellationToken = default)
    {
        var localDay = (asOfLocalDate ?? DateTime.Today).Date;
        var (fromUtc, toUtcExclusive) = ResolveWindow(period, localDay);

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        const string taxSql = """
            SELECT
                UPPER(LTRIM(RTRIM(ISNULL(JSON_VALUE(tax.value, '$.rateId'), 'UNKNOWN')))) AS TaxRateId,
                SUM(ISNULL(TRY_CAST(JSON_VALUE(tax.value, '$.taxableAmount') AS DECIMAL(18,2)), 0)) AS TaxableTotal,
                SUM(ISNULL(TRY_CAST(JSON_VALUE(tax.value, '$.taxAmount') AS DECIMAL(18,2)), 0)) AS VatCollected,
                COUNT(DISTINCT q.Id) AS InvoiceCount
            FROM dbo.OfflineInvoiceQueue AS q
            CROSS APPLY OPENJSON(q.PayloadJson, '$.invoiceSummary.taxBreakDown') AS tax
            WHERE q.Status = N'SYNCED'
              AND q.CreatedAt >= @FromUtc
              AND q.CreatedAt < @ToUtc
            GROUP BY UPPER(LTRIM(RTRIM(ISNULL(JSON_VALUE(tax.value, '$.rateId'), 'UNKNOWN'))))
            ORDER BY TaxRateId;
            """;

        var buckets = (await connection.QueryAsync<TaxCodeBucketRow>(
                new CommandDefinition(
                    taxSql,
                    new { FromUtc = fromUtc, ToUtc = toUtcExclusive },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).AsList();

        const string totalsSql = """
            SELECT
                COUNT(*) AS SyncedInvoiceCount,
                SUM(ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.invoiceTotal') AS DECIMAL(18,2)), 0)) AS GrossSales,
                SUM(ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.totalVAT') AS DECIMAL(18,2)), 0)) AS TotalVatDeclared
            FROM dbo.OfflineInvoiceQueue
            WHERE Status = N'SYNCED'
              AND CreatedAt >= @FromUtc
              AND CreatedAt < @ToUtc;
            """;

        var totals = await connection.QuerySingleAsync<PeriodTotalsRow>(
            new CommandDefinition(
                totalsSql,
                new { FromUtc = fromUtc, ToUtc = toUtcExclusive },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var categorized = buckets.Select(b => Categorize(b)).ToList();
        var taxableStandard = categorized
            .Where(c => c.Category == TaxCategory.Standard)
            .Sum(c => c.TaxableTotal);
        var vatCollected = categorized.Sum(c => c.VatCollected);
        var expectedVat = PosTaxCalculator.CalculateVatAmount(
            taxableStandard,
            PosTaxCalculator.MalawiStandardVatRatePercent);
        var variance = PosTaxCalculator.RoundMoney(vatCollected - expectedVat);

        return new TaxReconciliationReport
        {
            Period = period,
            FromUtc = fromUtc,
            ToUtcExclusive = toUtcExclusive,
            LocalBusinessDate = localDay,
            SyncedInvoiceCount = totals.SyncedInvoiceCount,
            GrossSales = totals.GrossSales,
            TotalVatDeclared = totals.TotalVatDeclared,
            TaxBuckets = categorized,
            StandardRateTaxable = taxableStandard,
            ExpectedStandardVat = expectedVat,
            ActualVatCollected = vatCollected,
            VatVariance = variance,
            IsBalanced = Math.Abs(variance) < 0.01m
        };
    }

    public async Task<IReadOnlyList<HourlySalesPoint>> GetHourlySalesVelocityAsync(
        DateTime localDay,
        CancellationToken cancellationToken = default)
    {
        var fromUtc = localDay.Date.ToUniversalTime();
        var toUtc = fromUtc.AddDays(1);

        const string sql = """
            SELECT
                DATEPART(HOUR, CreatedAt) AS HourUtc,
                COUNT(*) AS InvoiceCount,
                SUM(ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.invoiceTotal') AS DECIMAL(18,2)), 0)) AS SalesTotal
            FROM dbo.OfflineInvoiceQueue
            WHERE Status = N'SYNCED'
              AND CreatedAt >= @FromUtc
              AND CreatedAt < @ToUtc
            GROUP BY DATEPART(HOUR, CreatedAt)
            ORDER BY HourUtc;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<HourlySalesPoint>(
            new CommandDefinition(sql, new { FromUtc = fromUtc, ToUtc = toUtc }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<QueueHealthSnapshot> GetQueueHealthAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT Status, COUNT(*) AS Count
            FROM dbo.OfflineInvoiceQueue
            GROUP BY Status;

            SELECT TOP (24)
                DATEADD(HOUR, DATEDIFF(HOUR, 0, CreatedAt), 0) AS HourBucketUtc,
                SUM(CASE WHEN Status = N'SYNCED' THEN 1 ELSE 0 END) AS SyncedCount,
                SUM(CASE WHEN Status IN (N'PENDING', N'SYNCING') THEN 1 ELSE 0 END) AS BacklogCount,
                SUM(CASE WHEN Status = N'QUARANTINED' THEN 1 ELSE 0 END) AS QuarantinedCount
            FROM dbo.OfflineInvoiceQueue
            WHERE CreatedAt >= DATEADD(HOUR, -24, GETUTCDATE())
            GROUP BY DATEADD(HOUR, DATEDIFF(HOUR, 0, CreatedAt), 0)
            ORDER BY HourBucketUtc;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var multi = await connection.QueryMultipleAsync(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var statusRows = (await multi.ReadAsync<(string Status, int Count)>().ConfigureAwait(false)).AsList();
        var hourly = (await multi.ReadAsync<QueueHourlyDrainagePoint>().ConfigureAwait(false)).AsList();

        return new QueueHealthSnapshot
        {
            PendingCount = statusRows.FirstOrDefault(x => x.Status.Equals("PENDING", StringComparison.OrdinalIgnoreCase)).Count,
            SyncingCount = statusRows.FirstOrDefault(x => x.Status.Equals("SYNCING", StringComparison.OrdinalIgnoreCase)).Count,
            SyncedCount = statusRows.FirstOrDefault(x => x.Status.Equals("SYNCED", StringComparison.OrdinalIgnoreCase)).Count,
            QuarantinedCount = statusRows.FirstOrDefault(x => x.Status.Equals("QUARANTINED", StringComparison.OrdinalIgnoreCase)).Count,
            HourlyDrainage = hourly
        };
    }

    private static (DateTime FromUtc, DateTime ToUtcExclusive) ResolveWindow(TaxReconciliationPeriod period, DateTime localDay)
    {
        var startLocal = period switch
        {
            TaxReconciliationPeriod.Weekly => localDay.AddDays(-(int)localDay.DayOfWeek),
            TaxReconciliationPeriod.Monthly => new DateTime(localDay.Year, localDay.Month, 1),
            _ => localDay
        };
        var endLocal = period switch
        {
            TaxReconciliationPeriod.Weekly => startLocal.AddDays(7),
            TaxReconciliationPeriod.Monthly => startLocal.AddMonths(1),
            _ => startLocal.AddDays(1)
        };
        return (startLocal.ToUniversalTime(), endLocal.ToUniversalTime());
    }

    private static TaxCodeBucket Categorize(TaxCodeBucketRow row)
    {
        var category = row.TaxRateId switch
        {
            "Z" or "ZR" or "ZERO" => TaxCategory.ZeroRated,
            "E" or "EX" or "EXEMPT" => TaxCategory.Exempt,
            "A" or "T" or "S" or "STD" => TaxCategory.Standard,
            _ => TaxCategory.Other
        };

        return new TaxCodeBucket
        {
            TaxRateId = row.TaxRateId,
            Category = category,
            TaxableTotal = row.TaxableTotal,
            VatCollected = row.VatCollected,
            InvoiceCount = row.InvoiceCount
        };
    }

    private sealed class TaxCodeBucketRow
    {
        public string TaxRateId { get; set; } = "UNKNOWN";
        public decimal TaxableTotal { get; set; }
        public decimal VatCollected { get; set; }
        public int InvoiceCount { get; set; }
    }

    private sealed class PeriodTotalsRow
    {
        public int SyncedInvoiceCount { get; set; }
        public decimal GrossSales { get; set; }
        public decimal TotalVatDeclared { get; set; }
    }
}

public enum TaxCategory
{
    Standard,
    ZeroRated,
    Exempt,
    Other
}

public sealed class TaxCodeBucket
{
    public required string TaxRateId { get; init; }
    public TaxCategory Category { get; init; }
    public decimal TaxableTotal { get; init; }
    public decimal VatCollected { get; init; }
    public int InvoiceCount { get; init; }
}

public sealed class TaxReconciliationReport
{
    public TaxReconciliationPeriod Period { get; init; }
    public DateTime FromUtc { get; init; }
    public DateTime ToUtcExclusive { get; init; }
    public DateTime LocalBusinessDate { get; init; }
    public int SyncedInvoiceCount { get; init; }
    public decimal GrossSales { get; init; }
    public decimal TotalVatDeclared { get; init; }
    public IReadOnlyList<TaxCodeBucket> TaxBuckets { get; init; } = Array.Empty<TaxCodeBucket>();
    public decimal StandardRateTaxable { get; init; }
    public decimal ExpectedStandardVat { get; init; }
    public decimal ActualVatCollected { get; init; }
    public decimal VatVariance { get; init; }
    public bool IsBalanced { get; init; }
}

public sealed class HourlySalesPoint
{
    public int HourUtc { get; set; }
    public int InvoiceCount { get; set; }
    public decimal SalesTotal { get; set; }
}

public sealed class QueueHourlyDrainagePoint
{
    public DateTime HourBucketUtc { get; set; }
    public int SyncedCount { get; set; }
    public int BacklogCount { get; set; }
    public int QuarantinedCount { get; set; }
}

public sealed class QueueHealthSnapshot
{
    public int PendingCount { get; init; }
    public int SyncingCount { get; init; }
    public int SyncedCount { get; init; }
    public int QuarantinedCount { get; init; }
    public IReadOnlyList<QueueHourlyDrainagePoint> HourlyDrainage { get; init; } = Array.Empty<QueueHourlyDrainagePoint>();
}
