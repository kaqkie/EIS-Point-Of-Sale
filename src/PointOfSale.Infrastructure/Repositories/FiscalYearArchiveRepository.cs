using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface IFiscalYearArchiveRepository
{
    Task<FiscalYearArchiveRecord?> GetByFiscalYearAsync(int fiscalYear, CancellationToken cancellationToken = default);
    Task<long> InsertArchiveAsync(FiscalYearArchiveRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FiscalYearArchiveRecord>> GetRecentArchivesAsync(int take = 20, CancellationToken cancellationToken = default);
    Task<long> InsertPackageAsync(FiscalArchivePackageRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FiscalArchivePackageRecord>> GetRecentPackagesAsync(int take = 30, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FinancialClosureRecord>> GetClosuresBetweenAsync(
        DateTime startInclusive,
        DateTime endInclusive,
        CancellationToken cancellationToken = default);
}

public sealed class FiscalYearArchiveRepository : IFiscalYearArchiveRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public FiscalYearArchiveRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<FiscalYearArchiveRecord?> GetByFiscalYearAsync(
        int fiscalYear,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (1)
                ArchiveId, FiscalYear, PeriodStart, PeriodEnd, RolledOverAtUtc,
                PrimarySupervisorUsername, SecondarySupervisorUsername,
                TotalGrossSalesMwk, TotalVatCollectedMwk, ExpectedClosureDays, ClosedDays,
                SyncedInvoiceCount, ManifestSha256, ManifestHmacSha512, ArchiveFilePath, ArchiveBytes,
                CryptographicVerificationPassed, Status, Notes
            FROM dbo.FiscalYearArchives
            WHERE FiscalYear = @FiscalYear AND Status = @Status
            ORDER BY ArchiveId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<FiscalYearArchiveRecord>(
                new CommandDefinition(
                    sql,
                    new { FiscalYear = fiscalYear, Status = FiscalYearArchiveStatuses.Locked },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<long> InsertArchiveAsync(FiscalYearArchiveRecord record, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.FiscalYearArchives
            (
                FiscalYear, PeriodStart, PeriodEnd, RolledOverAtUtc,
                PrimarySupervisorUsername, SecondarySupervisorUsername,
                TotalGrossSalesMwk, TotalVatCollectedMwk, ExpectedClosureDays, ClosedDays,
                SyncedInvoiceCount, ManifestSha256, ManifestHmacSha512, ArchiveFilePath, ArchiveBytes,
                CryptographicVerificationPassed, Status, Notes
            )
            OUTPUT INSERTED.ArchiveId
            VALUES
            (
                @FiscalYear, @PeriodStart, @PeriodEnd, @RolledOverAtUtc,
                @PrimarySupervisorUsername, @SecondarySupervisorUsername,
                @TotalGrossSalesMwk, @TotalVatCollectedMwk, @ExpectedClosureDays, @ClosedDays,
                @SyncedInvoiceCount, @ManifestSha256, @ManifestHmacSha512, @ArchiveFilePath, @ArchiveBytes,
                @CryptographicVerificationPassed, @Status, @Notes
            );
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(sql, record, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FiscalYearArchiveRecord>> GetRecentArchivesAsync(
        int take = 20,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                ArchiveId, FiscalYear, PeriodStart, PeriodEnd, RolledOverAtUtc,
                PrimarySupervisorUsername, SecondarySupervisorUsername,
                TotalGrossSalesMwk, TotalVatCollectedMwk, ExpectedClosureDays, ClosedDays,
                SyncedInvoiceCount, ManifestSha256, ManifestHmacSha512, ArchiveFilePath, ArchiveBytes,
                CryptographicVerificationPassed, Status, Notes
            FROM dbo.FiscalYearArchives
            ORDER BY FiscalYear DESC, ArchiveId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<FiscalYearArchiveRecord>(
                new CommandDefinition(sql, new { Take = Math.Clamp(take, 1, 100) }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<long> InsertPackageAsync(FiscalArchivePackageRecord record, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.FiscalArchivePackages
                (PackageType, PeriodStartUtc, PeriodEndUtc, FilePath, FileBytes, ContentSha256, TriggeredByUsername, DualKeyProtected)
            OUTPUT INSERTED.PackageId
            VALUES
                (@PackageType, @PeriodStartUtc, @PeriodEndUtc, @FilePath, @FileBytes, @ContentSha256, @TriggeredByUsername, @DualKeyProtected);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(sql, record, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FiscalArchivePackageRecord>> GetRecentPackagesAsync(
        int take = 30,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                PackageId, CreatedAtUtc, PackageType, PeriodStartUtc, PeriodEndUtc,
                FilePath, FileBytes, ContentSha256, TriggeredByUsername, DualKeyProtected
            FROM dbo.FiscalArchivePackages
            ORDER BY CreatedAtUtc DESC, PackageId DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<FiscalArchivePackageRecord>(
                new CommandDefinition(sql, new { Take = Math.Clamp(take, 1, 200) }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<FinancialClosureRecord>> GetClosuresBetweenAsync(
        DateTime startInclusive,
        DateTime endInclusive,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                ClosureId, BusinessDate, ClosedAtUtc, ClosedByUsername, ClosedByDisplayName,
                TotalGrossSalesMwk, TotalTaxableSalesMwk, TotalVatCollectedMwk, ExpectedVatMwk, VatVarianceMwk,
                CashCollectionsMwk, CardSettlementsMwk, MobileMoneySettlementsMwk, OtherSettlementsMwk,
                TotalVoidsMwk, VoidCount, SyncedInvoiceCount, PendingInvoiceCount, QuarantinedInvoiceCount,
                FiscalSignatureMatchCount, FiscalSignatureMissingCount, CashDrawerVarianceMwk,
                CumulativeGrossSalesMwk, CumulativeVatMwk, ShiftCount, AuditPassed, Status, Notes, ClosureJson
            FROM dbo.FinancialClosures
            WHERE BusinessDate >= @Start AND BusinessDate <= @End AND Status = 'Closed'
            ORDER BY BusinessDate ASC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<FinancialClosureRecord>(
                new CommandDefinition(
                    sql,
                    new { Start = startInclusive.Date, End = endInclusive.Date },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}
