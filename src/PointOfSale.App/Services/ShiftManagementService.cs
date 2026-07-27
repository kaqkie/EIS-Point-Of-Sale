using System.Text.Json;
using Dapper;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.Services;

public interface IShiftManagementService
{
    Task<CashierShift?> GetOpenShiftAsync(CancellationToken cancellationToken = default);
    Task<CashierShift> OpenShiftAsync(string cashierName, decimal openingFloat, CancellationToken cancellationToken = default);
    Task<ShiftCashMovement> RecordCashInAsync(decimal amount, string? reason, CancellationToken cancellationToken = default);
    Task<ShiftCashMovement> RecordCashOutAsync(decimal amount, string? reason, CancellationToken cancellationToken = default);
    Task<ShiftCashMovement> RecordCashDropAsync(decimal amount, string? reason, CancellationToken cancellationToken = default);
    Task<ZReportBundle> CloseShiftAsync(decimal closingCashCounted, string? notes = null, CancellationToken cancellationToken = default);
    Task<ZReportBundle?> BuildZReportPreviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CashierShift>> GetRecentShiftsAsync(int take = 20, CancellationToken cancellationToken = default);
}

public sealed class ShiftManagementService : IShiftManagementService
{
    private readonly ICashierShiftRepository _shiftRepository;
    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly IDatabaseBackupService? _backupService;

    public ShiftManagementService(
        ICashierShiftRepository shiftRepository,
        ISqlConnectionFactory connectionFactory,
        IDatabaseBackupService? backupService = null)
    {
        _shiftRepository = shiftRepository;
        _connectionFactory = connectionFactory;
        _backupService = backupService;
    }

    public Task<CashierShift?> GetOpenShiftAsync(CancellationToken cancellationToken = default) =>
        _shiftRepository.GetOpenShiftAsync(cancellationToken);

    public async Task<CashierShift> OpenShiftAsync(
        string cashierName,
        decimal openingFloat,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cashierName);
        if (openingFloat < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(openingFloat), "Opening float cannot be negative.");
        }

        var existing = await _shiftRepository.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Shift {existing.ShiftId} is already open for {existing.CashierName}. Close it before opening another.");
        }

        var id = await _shiftRepository.OpenShiftAsync(cashierName.Trim(), openingFloat, cancellationToken)
            .ConfigureAwait(false);
        return (await _shiftRepository.GetByIdAsync(id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<ShiftCashMovement> RecordCashInAsync(
        decimal amount,
        string? reason,
        CancellationToken cancellationToken = default) =>
        await AddMovementAsync(ShiftCashMovementTypes.CashIn, amount, reason, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ShiftCashMovement> RecordCashOutAsync(
        decimal amount,
        string? reason,
        CancellationToken cancellationToken = default) =>
        await AddMovementAsync(ShiftCashMovementTypes.CashOut, amount, reason, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ShiftCashMovement> RecordCashDropAsync(
        decimal amount,
        string? reason,
        CancellationToken cancellationToken = default) =>
        await AddMovementAsync(ShiftCashMovementTypes.CashDrop, amount, reason, cancellationToken)
            .ConfigureAwait(false);

    public async Task<ZReportBundle> CloseShiftAsync(
        decimal closingCashCounted,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (closingCashCounted < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(closingCashCounted));
        }

        var shift = await _shiftRepository.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No open shift to close.");

        var report = await BuildZReportAsync(shift, cancellationToken).ConfigureAwait(false);
        var expectedCash = report.ExpectedCashInDrawer;
        var variance = PosTaxCalculator.RoundMoney(closingCashCounted - expectedCash);
        var json = JsonSerializer.Serialize(report, MraJson.SerializerOptions);

        await _shiftRepository.CloseShiftAsync(
                shift.ShiftId,
                closingCashCounted,
                expectedCash,
                variance,
                json,
                notes,
                cancellationToken)
            .ConfigureAwait(false);

        report = report with
        {
            ClosingCashCounted = closingCashCounted,
            CashVariance = variance,
            ClosedAtUtc = DateTime.UtcNow
        };

        if (_backupService is not null)
        {
            try
            {
                await _backupService.BackupOnShiftCloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Disaster-recovery backup must never block shift close.
            }
        }

        return report;
    }

    public async Task<ZReportBundle?> BuildZReportPreviewAsync(CancellationToken cancellationToken = default)
    {
        var shift = await _shiftRepository.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false);
        if (shift is null)
        {
            return null;
        }

        return await BuildZReportAsync(shift, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CashierShift>> GetRecentShiftsAsync(
        int take = 20,
        CancellationToken cancellationToken = default) =>
        _shiftRepository.GetRecentShiftsAsync(take, cancellationToken);

    private async Task<ShiftCashMovement> AddMovementAsync(
        string type,
        decimal amount,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "Amount must be greater than zero.");
        }

        var shift = await _shiftRepository.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Open a shift before recording cash movements.");

        var id = await _shiftRepository
            .AddCashMovementAsync(shift.ShiftId, type, amount, reason, cancellationToken)
            .ConfigureAwait(false);

        var movements = await _shiftRepository.GetMovementsAsync(shift.ShiftId, cancellationToken).ConfigureAwait(false);
        return movements.First(m => m.MovementId == id);
    }

    private async Task<ZReportBundle> BuildZReportAsync(CashierShift shift, CancellationToken cancellationToken)
    {
        var toUtc = shift.ClosedAtUtc ?? DateTime.UtcNow;
        var movements = await _shiftRepository.GetMovementsAsync(shift.ShiftId, cancellationToken).ConfigureAwait(false);

        const string salesSql = """
            SELECT
                JSON_VALUE(PayloadJson, '$.invoiceHeader.invoiceNumber') AS InvoiceNumber,
                JSON_VALUE(PayloadJson, '$.invoiceHeader.paymentMethod') AS PaymentMethod,
                ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.invoiceTotal') AS DECIMAL(18,2)), 0) AS InvoiceTotal,
                ISNULL(TRY_CAST(JSON_VALUE(PayloadJson, '$.invoiceSummary.totalVAT') AS DECIMAL(18,2)), 0) AS TotalVat,
                ISNULL(JSON_VALUE(FiscalResponseJson, '$.fiscalSignature'), JSON_VALUE(FiscalResponseJson, '$.fiscalCode')) AS FiscalSignature
            FROM dbo.OfflineInvoiceQueue
            WHERE Status = N'SYNCED'
              AND CreatedAt >= @FromUtc
              AND CreatedAt <= @ToUtc
            ORDER BY CreatedAt DESC, Id DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var invoices = (await connection.QueryAsync<ZReportInvoiceLine>(
                new CommandDefinition(
                    salesSql,
                    new { FromUtc = shift.OpenedAtUtc, ToUtc = toUtc },
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).AsList();

        decimal Sum(string method) =>
            invoices
                .Where(i => string.Equals(i.PaymentMethod, method, StringComparison.OrdinalIgnoreCase))
                .Sum(i => i.InvoiceTotal);

        var cashSales = Sum("Cash");
        var cardSales = Sum("Card");
        var mobileSales = Sum("MobileMoney");
        var otherSales = invoices.Sum(i => i.InvoiceTotal) - cashSales - cardSales - mobileSales;

        var cashIn = movements.Where(m => m.MovementType == ShiftCashMovementTypes.CashIn).Sum(m => m.Amount);
        var cashOut = movements
            .Where(m => m.MovementType is ShiftCashMovementTypes.CashOut or ShiftCashMovementTypes.CashDrop)
            .Sum(m => m.Amount);
        var cashDrops = movements.Where(m => m.MovementType == ShiftCashMovementTypes.CashDrop).Sum(m => m.Amount);
        var expectedCash = PosTaxCalculator.RoundMoney(shift.OpeningFloat + cashSales + cashIn - cashOut);

        return new ZReportBundle
        {
            ShiftId = shift.ShiftId,
            CashierName = shift.CashierName,
            OpenedAtUtc = shift.OpenedAtUtc,
            ClosedAtUtc = shift.ClosedAtUtc,
            OpeningFloat = shift.OpeningFloat,
            CashSales = cashSales,
            CardSales = cardSales,
            MobileMoneySales = mobileSales,
            OtherSales = otherSales,
            GrossSales = invoices.Sum(i => i.InvoiceTotal),
            TotalVat = invoices.Sum(i => i.TotalVat),
            CashInTotal = cashIn,
            CashOutTotal = cashOut - cashDrops,
            CashDropTotal = cashDrops,
            ExpectedCashInDrawer = expectedCash,
            ClosingCashCounted = shift.ClosingCashCounted,
            CashVariance = shift.CashVariance,
            InvoiceCount = invoices.Count,
            FiscalizedInvoices = invoices
        };
    }
}

public sealed record ZReportBundle
{
    public int ShiftId { get; init; }
    public required string CashierName { get; init; }
    public DateTime OpenedAtUtc { get; init; }
    public DateTime? ClosedAtUtc { get; init; }
    public decimal OpeningFloat { get; init; }
    public decimal CashSales { get; init; }
    public decimal CardSales { get; init; }
    public decimal MobileMoneySales { get; init; }
    public decimal OtherSales { get; init; }
    public decimal GrossSales { get; init; }
    public decimal TotalVat { get; init; }
    public decimal CashInTotal { get; init; }
    public decimal CashOutTotal { get; init; }
    public decimal CashDropTotal { get; init; }
    public decimal ExpectedCashInDrawer { get; init; }
    public decimal? ClosingCashCounted { get; init; }
    public decimal? CashVariance { get; init; }
    public int InvoiceCount { get; init; }
    public IReadOnlyList<ZReportInvoiceLine> FiscalizedInvoices { get; init; } = Array.Empty<ZReportInvoiceLine>();
}

public sealed class ZReportInvoiceLine
{
    public string? InvoiceNumber { get; set; }
    public string? PaymentMethod { get; set; }
    public decimal InvoiceTotal { get; set; }
    public decimal TotalVat { get; set; }
    public string? FiscalSignature { get; set; }
}
