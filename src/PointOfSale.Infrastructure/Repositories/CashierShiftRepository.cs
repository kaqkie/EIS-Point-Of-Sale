using Dapper;
using Microsoft.Data.SqlClient;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface ICashierShiftRepository
{
    Task<CashierShift?> GetOpenShiftAsync(CancellationToken cancellationToken = default);
    Task<CashierShift?> GetByIdAsync(int shiftId, CancellationToken cancellationToken = default);
    Task<int> OpenShiftAsync(string cashierName, decimal openingFloat, CancellationToken cancellationToken = default);
    Task CloseShiftAsync(int shiftId, decimal closingCashCounted, decimal expectedCash, decimal variance, string zReportJson, string? notes, CancellationToken cancellationToken = default);
    Task<int> AddCashMovementAsync(int shiftId, string movementType, decimal amount, string? reason, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ShiftCashMovement>> GetMovementsAsync(int shiftId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CashierShift>> GetRecentShiftsAsync(int take, CancellationToken cancellationToken = default);
}

public sealed class CashierShiftRepository : ICashierShiftRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public CashierShiftRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<CashierShift?> GetOpenShiftAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (1)
                ShiftId, OpenedAtUtc, ClosedAtUtc, CashierName, OpeningFloat,
                ClosingCashCounted, ExpectedCash, CashVariance, Status, ZReportJson, Notes
            FROM dbo.CashierShifts
            WHERE Status = @Status
            ORDER BY OpenedAtUtc DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<CashierShift>(
            new CommandDefinition(sql, new { Status = ShiftStatuses.Open }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<CashierShift?> GetByIdAsync(int shiftId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT ShiftId, OpenedAtUtc, ClosedAtUtc, CashierName, OpeningFloat,
                   ClosingCashCounted, ExpectedCash, CashVariance, Status, ZReportJson, Notes
            FROM dbo.CashierShifts
            WHERE ShiftId = @ShiftId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<CashierShift>(
            new CommandDefinition(sql, new { ShiftId = shiftId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<int> OpenShiftAsync(string cashierName, decimal openingFloat, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.CashierShifts (CashierName, OpeningFloat, Status)
            OUTPUT INSERTED.ShiftId
            VALUES (@CashierName, @OpeningFloat, @Status);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { CashierName = cashierName, OpeningFloat = openingFloat, Status = ShiftStatuses.Open },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task CloseShiftAsync(
        int shiftId,
        decimal closingCashCounted,
        decimal expectedCash,
        decimal variance,
        string zReportJson,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.CashierShifts
            SET ClosedAtUtc = SYSUTCDATETIME(),
                ClosingCashCounted = @ClosingCashCounted,
                ExpectedCash = @ExpectedCash,
                CashVariance = @Variance,
                ZReportJson = @ZReportJson,
                Notes = @Notes,
                Status = @ClosedStatus
            WHERE ShiftId = @ShiftId AND Status = @OpenStatus;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    ShiftId = shiftId,
                    ClosingCashCounted = closingCashCounted,
                    ExpectedCash = expectedCash,
                    Variance = variance,
                    ZReportJson = zReportJson,
                    Notes = notes,
                    ClosedStatus = ShiftStatuses.Closed,
                    OpenStatus = ShiftStatuses.Open
                },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        if (rows != 1)
        {
            throw new InvalidOperationException($"Shift {shiftId} is not open or could not be closed.");
        }
    }

    public async Task<int> AddCashMovementAsync(
        int shiftId,
        string movementType,
        decimal amount,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.ShiftCashMovements (ShiftId, MovementType, Amount, Reason)
            OUTPUT INSERTED.MovementId
            VALUES (@ShiftId, @MovementType, @Amount, @Reason);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { ShiftId = shiftId, MovementType = movementType, Amount = amount, Reason = reason },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ShiftCashMovement>> GetMovementsAsync(
        int shiftId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT MovementId, ShiftId, MovementType, Amount, Reason, CreatedAtUtc
            FROM dbo.ShiftCashMovements
            WHERE ShiftId = @ShiftId
            ORDER BY CreatedAtUtc ASC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<ShiftCashMovement>(
            new CommandDefinition(sql, new { ShiftId = shiftId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<CashierShift>> GetRecentShiftsAsync(int take, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                ShiftId, OpenedAtUtc, ClosedAtUtc, CashierName, OpeningFloat,
                ClosingCashCounted, ExpectedCash, CashVariance, Status, ZReportJson, Notes
            FROM dbo.CashierShifts
            ORDER BY OpenedAtUtc DESC;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<CashierShift>(
            new CommandDefinition(sql, new { Take = take }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}
