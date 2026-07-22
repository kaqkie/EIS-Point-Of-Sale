using System.Text.Json;
using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface IFinancialClosureRepository
{
    Task<FinancialClosureRecord?> GetByBusinessDateAsync(DateTime businessDate, CancellationToken cancellationToken = default);

    Task<long> InsertAsync(FinancialClosureRecord record, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FinancialClosureRecord>> GetRecentAsync(int take = 30, CancellationToken cancellationToken = default);

    Task<(decimal CumulativeGross, decimal CumulativeVat)> GetCumulativeTotalsBeforeAsync(
        DateTime businessDate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CashierShift>> GetShiftsForUtcWindowAsync(
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken = default);
}

public sealed class FinancialClosureRepository : IFinancialClosureRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public FinancialClosureRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<FinancialClosureRecord?> GetByBusinessDateAsync(
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (1)
                ClosureId, BusinessDate, ClosedAtUtc, ClosedByUsername, ClosedByDisplayName,
                TotalGrossSalesMwk, TotalTaxableSalesMwk, TotalVatCollectedMwk, ExpectedVatMwk, VatVarianceMwk,
                CashCollectionsMwk, CardSettlementsMwk, MobileMoneySettlementsMwk, OtherSettlementsMwk,
                TotalVoidsMwk, VoidCount, SyncedInvoiceCount, PendingInvoiceCount, QuarantinedInvoiceCount,
                FiscalSignatureMatchCount, FiscalSignatureMissingCount, CashDrawerVarianceMwk,
                CumulativeGrossSalesMwk, CumulativeVatMwk, ShiftCount, AuditPassed, Status, Notes, ClosureJson
            FROM dbo.FinancialClosures
            WHERE BusinessDate = @BusinessDate
              AND Status = @Status
            ORDER BY ClosureId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<FinancialClosureRecord>(
                new CommandDefinition(
                    sql,
                    new { BusinessDate = businessDate.Date, Status = FinancialClosureStatuses.Closed },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<long> InsertAsync(FinancialClosureRecord record, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.FinancialClosures
            (
                BusinessDate, ClosedAtUtc, ClosedByUsername, ClosedByDisplayName,
                TotalGrossSalesMwk, TotalTaxableSalesMwk, TotalVatCollectedMwk, ExpectedVatMwk, VatVarianceMwk,
                CashCollectionsMwk, CardSettlementsMwk, MobileMoneySettlementsMwk, OtherSettlementsMwk,
                TotalVoidsMwk, VoidCount, SyncedInvoiceCount, PendingInvoiceCount, QuarantinedInvoiceCount,
                FiscalSignatureMatchCount, FiscalSignatureMissingCount, CashDrawerVarianceMwk,
                CumulativeGrossSalesMwk, CumulativeVatMwk, ShiftCount, AuditPassed, Status, Notes, ClosureJson
            )
            OUTPUT INSERTED.ClosureId
            VALUES
            (
                @BusinessDate, @ClosedAtUtc, @ClosedByUsername, @ClosedByDisplayName,
                @TotalGrossSalesMwk, @TotalTaxableSalesMwk, @TotalVatCollectedMwk, @ExpectedVatMwk, @VatVarianceMwk,
                @CashCollectionsMwk, @CardSettlementsMwk, @MobileMoneySettlementsMwk, @OtherSettlementsMwk,
                @TotalVoidsMwk, @VoidCount, @SyncedInvoiceCount, @PendingInvoiceCount, @QuarantinedInvoiceCount,
                @FiscalSignatureMatchCount, @FiscalSignatureMissingCount, @CashDrawerVarianceMwk,
                @CumulativeGrossSalesMwk, @CumulativeVatMwk, @ShiftCount, @AuditPassed, @Status, @Notes, @ClosureJson
            );
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(sql, record, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FinancialClosureRecord>> GetRecentAsync(
        int take = 30,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                ClosureId, BusinessDate, ClosedAtUtc, ClosedByUsername, ClosedByDisplayName,
                TotalGrossSalesMwk, TotalTaxableSalesMwk, TotalVatCollectedMwk, ExpectedVatMwk, VatVarianceMwk,
                CashCollectionsMwk, CardSettlementsMwk, MobileMoneySettlementsMwk, OtherSettlementsMwk,
                TotalVoidsMwk, VoidCount, SyncedInvoiceCount, PendingInvoiceCount, QuarantinedInvoiceCount,
                FiscalSignatureMatchCount, FiscalSignatureMissingCount, CashDrawerVarianceMwk,
                CumulativeGrossSalesMwk, CumulativeVatMwk, ShiftCount, AuditPassed, Status, Notes, ClosureJson
            FROM dbo.FinancialClosures
            ORDER BY BusinessDate DESC, ClosureId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<FinancialClosureRecord>(
                new CommandDefinition(
                    sql,
                    new { Take = Math.Clamp(take, 1, 200) },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<(decimal CumulativeGross, decimal CumulativeVat)> GetCumulativeTotalsBeforeAsync(
        DateTime businessDate,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                ISNULL(SUM(TotalGrossSalesMwk), 0) AS CumulativeGross,
                ISNULL(SUM(TotalVatCollectedMwk), 0) AS CumulativeVat
            FROM dbo.FinancialClosures
            WHERE BusinessDate < @BusinessDate
              AND Status = @Status;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleAsync<(decimal CumulativeGross, decimal CumulativeVat)>(
                new CommandDefinition(
                    sql,
                    new { BusinessDate = businessDate.Date, Status = FinancialClosureStatuses.Closed },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<CashierShift>> GetShiftsForUtcWindowAsync(
        DateTime fromUtc,
        DateTime toUtcExclusive,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                ShiftId, OpenedAtUtc, ClosedAtUtc, CashierName, OpeningFloat,
                ClosingCashCounted, ExpectedCash, CashVariance, Status, ZReportJson, Notes
            FROM dbo.CashierShifts
            WHERE OpenedAtUtc < @ToUtc
              AND (ClosedAtUtc IS NULL OR ClosedAtUtc >= @FromUtc)
            ORDER BY OpenedAtUtc ASC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<CashierShift>(
                new CommandDefinition(
                    sql,
                    new { FromUtc = fromUtc, ToUtc = toUtcExclusive },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}

public static class FinancialClosureJson
{
    public static string Serialize(object value) => JsonSerializer.Serialize(value);
}
