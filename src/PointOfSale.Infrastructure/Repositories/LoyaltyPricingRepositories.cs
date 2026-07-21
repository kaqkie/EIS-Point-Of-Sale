using Dapper;
using Microsoft.Data.SqlClient;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface ILoyaltyMemberRepository
{
    Task<LoyaltyMember?> GetByIdAsync(int memberId, CancellationToken cancellationToken = default);
    Task<LoyaltyMember?> GetByCodeAsync(string memberCode, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LoyaltyMember>> SearchAsync(string query, int take = 50, CancellationToken cancellationToken = default);
    Task<int> CreateAsync(LoyaltyMember member, CancellationToken cancellationToken = default);
    Task UpdateProfileAsync(int memberId, string fullName, string? phone, bool isActive, CancellationToken cancellationToken = default);
    Task<(bool Success, decimal NewBalance)> TryRedeemPointsAsync(
        int memberId,
        decimal points,
        decimal amountMwk,
        string? invoiceNumber,
        string? notes,
        CancellationToken cancellationToken = default);
    Task EarnPointsAsync(
        int memberId,
        decimal points,
        decimal amountMwk,
        string? invoiceNumber,
        string? notes,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LoyaltyLedgerEntry>> GetLedgerAsync(int memberId, int take = 50, CancellationToken cancellationToken = default);
}

public sealed class LoyaltyMemberRepository : ILoyaltyMemberRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public LoyaltyMemberRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<LoyaltyMember?> GetByIdAsync(int memberId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT MemberId, MemberCode, FullName, Phone, PointsBalance, LifetimeSpendMwk,
                   IsActive, CreatedAtUtc, LastPurchaseAtUtc
            FROM dbo.LoyaltyMembers WHERE MemberId = @MemberId;
            """;
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<LoyaltyMember>(
            new CommandDefinition(sql, new { MemberId = memberId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<LoyaltyMember?> GetByCodeAsync(string memberCode, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT MemberId, MemberCode, FullName, Phone, PointsBalance, LifetimeSpendMwk,
                   IsActive, CreatedAtUtc, LastPurchaseAtUtc
            FROM dbo.LoyaltyMembers WHERE MemberCode = @MemberCode;
            """;
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<LoyaltyMember>(
            new CommandDefinition(sql, new { MemberCode = memberCode }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LoyaltyMember>> SearchAsync(
        string query,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var pattern = $"%{query.Trim()}%";
        const string sql = """
            SELECT TOP (@Take)
                MemberId, MemberCode, FullName, Phone, PointsBalance, LifetimeSpendMwk,
                IsActive, CreatedAtUtc, LastPurchaseAtUtc
            FROM dbo.LoyaltyMembers
            WHERE IsActive = 1
              AND (MemberCode LIKE @Pattern OR FullName LIKE @Pattern OR ISNULL(Phone, N'') LIKE @Pattern)
            ORDER BY FullName;
            """;
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<LoyaltyMember>(
            new CommandDefinition(sql, new { Take = take, Pattern = pattern }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<int> CreateAsync(LoyaltyMember member, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.LoyaltyMembers (MemberCode, FullName, Phone, PointsBalance, LifetimeSpendMwk, IsActive)
            OUTPUT INSERTED.MemberId
            VALUES (@MemberCode, @FullName, @Phone, @PointsBalance, @LifetimeSpendMwk, @IsActive);
            """;
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, member, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateProfileAsync(
        int memberId,
        string fullName,
        string? phone,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.LoyaltyMembers
            SET FullName = @FullName, Phone = @Phone, IsActive = @IsActive
            WHERE MemberId = @MemberId;
            """;
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { MemberId = memberId, FullName = fullName, Phone = phone, IsActive = isActive },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<(bool Success, decimal NewBalance)> TryRedeemPointsAsync(
        int memberId,
        decimal points,
        decimal amountMwk,
        string? invoiceNumber,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        const string updateSql = """
            UPDATE dbo.LoyaltyMembers
            SET PointsBalance = PointsBalance - @Points
            WHERE MemberId = @MemberId AND PointsBalance >= @Points AND IsActive = 1;
            """;
        var updated = await connection.ExecuteAsync(
            new CommandDefinition(
                updateSql,
                new { MemberId = memberId, Points = points },
                transaction: tx,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (updated != 1)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return (false, 0m);
        }

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO dbo.LoyaltyLedger (MemberId, EntryType, Points, AmountMwk, InvoiceNumber, Notes)
                VALUES (@MemberId, @EntryType, @Points, @AmountMwk, @InvoiceNumber, @Notes);
                """,
                new
                {
                    MemberId = memberId,
                    EntryType = LoyaltyLedgerTypes.Redeem,
                    Points = points,
                    AmountMwk = amountMwk,
                    InvoiceNumber = invoiceNumber,
                    Notes = notes
                },
                transaction: tx,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var balance = await connection.ExecuteScalarAsync<decimal>(
            new CommandDefinition(
                "SELECT PointsBalance FROM dbo.LoyaltyMembers WHERE MemberId = @MemberId;",
                new { MemberId = memberId },
                transaction: tx,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return (true, balance);
    }

    public async Task EarnPointsAsync(
        int memberId,
        decimal points,
        decimal amountMwk,
        string? invoiceNumber,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE dbo.LoyaltyMembers
                SET PointsBalance = PointsBalance + @Points,
                    LifetimeSpendMwk = LifetimeSpendMwk + @AmountMwk,
                    LastPurchaseAtUtc = SYSUTCDATETIME()
                WHERE MemberId = @MemberId AND IsActive = 1;
                """,
                new { MemberId = memberId, Points = points, AmountMwk = amountMwk },
                transaction: tx,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                INSERT INTO dbo.LoyaltyLedger (MemberId, EntryType, Points, AmountMwk, InvoiceNumber, Notes)
                VALUES (@MemberId, @EntryType, @Points, @AmountMwk, @InvoiceNumber, @Notes);
                """,
                new
                {
                    MemberId = memberId,
                    EntryType = LoyaltyLedgerTypes.Earn,
                    Points = points,
                    AmountMwk = amountMwk,
                    InvoiceNumber = invoiceNumber,
                    Notes = notes
                },
                transaction: tx,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LoyaltyLedgerEntry>> GetLedgerAsync(
        int memberId,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT TOP (@Take)
                LedgerId, MemberId, EntryType, Points, AmountMwk, InvoiceNumber, Notes, CreatedAtUtc
            FROM dbo.LoyaltyLedger
            WHERE MemberId = @MemberId
            ORDER BY CreatedAtUtc DESC, LedgerId DESC;
            """;
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<LoyaltyLedgerEntry>(
            new CommandDefinition(sql, new { MemberId = memberId, Take = take }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }
}

public interface IPricingRuleRepository
{
    Task<IReadOnlyList<PricingRule>> GetActiveAsync(DateTime asOfUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PricingRule>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<int> CreateAsync(PricingRule rule, CancellationToken cancellationToken = default);
    Task UpdateAsync(PricingRule rule, CancellationToken cancellationToken = default);
    Task SetActiveAsync(int ruleId, bool isActive, CancellationToken cancellationToken = default);
}

public sealed class PricingRuleRepository : IPricingRuleRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public PricingRuleRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<PricingRule>> GetActiveAsync(
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT RuleId, Name, RuleType, CategoryCode, ProductCode, PercentOff, BuyQuantity, FreeQuantity,
                   PromoUnitPrice, StartsAtUtc, EndsAtUtc, Priority, IsActive, CreatedAtUtc
            FROM dbo.PricingRules
            WHERE IsActive = 1
              AND StartsAtUtc <= @AsOfUtc
              AND (EndsAtUtc IS NULL OR EndsAtUtc > @AsOfUtc)
            ORDER BY Priority DESC, RuleId ASC;
            """;
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<PricingRule>(
            new CommandDefinition(sql, new { AsOfUtc = asOfUtc }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PricingRule>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT RuleId, Name, RuleType, CategoryCode, ProductCode, PercentOff, BuyQuantity, FreeQuantity,
                   PromoUnitPrice, StartsAtUtc, EndsAtUtc, Priority, IsActive, CreatedAtUtc
            FROM dbo.PricingRules
            ORDER BY Priority DESC, RuleId ASC;
            """;
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<PricingRule>(
            new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<int> CreateAsync(PricingRule rule, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.PricingRules
                (Name, RuleType, CategoryCode, ProductCode, PercentOff, BuyQuantity, FreeQuantity,
                 PromoUnitPrice, StartsAtUtc, EndsAtUtc, Priority, IsActive)
            OUTPUT INSERTED.RuleId
            VALUES
                (@Name, @RuleType, @CategoryCode, @ProductCode, @PercentOff, @BuyQuantity, @FreeQuantity,
                 @PromoUnitPrice, @StartsAtUtc, @EndsAtUtc, @Priority, @IsActive);
            """;
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, rule, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateAsync(PricingRule rule, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.PricingRules
            SET Name = @Name,
                RuleType = @RuleType,
                CategoryCode = @CategoryCode,
                ProductCode = @ProductCode,
                PercentOff = @PercentOff,
                BuyQuantity = @BuyQuantity,
                FreeQuantity = @FreeQuantity,
                PromoUnitPrice = @PromoUnitPrice,
                StartsAtUtc = @StartsAtUtc,
                EndsAtUtc = @EndsAtUtc,
                Priority = @Priority,
                IsActive = @IsActive
            WHERE RuleId = @RuleId;
            """;
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(sql, rule, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task SetActiveAsync(int ruleId, bool isActive, CancellationToken cancellationToken = default)
    {
        const string sql = "UPDATE dbo.PricingRules SET IsActive = @IsActive WHERE RuleId = @RuleId;";
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { RuleId = ruleId, IsActive = isActive }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
