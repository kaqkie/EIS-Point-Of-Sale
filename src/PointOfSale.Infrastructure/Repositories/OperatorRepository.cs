using Dapper;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.Infrastructure.Repositories;

public interface IOperatorRepository
{
    Task<OperatorAccount?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default);
    Task<OperatorAccount?> GetByIdAsync(int operatorId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperatorAccount>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<int> CountAsync(CancellationToken cancellationToken = default);
    Task<int> CreateAsync(OperatorAccount account, CancellationToken cancellationToken = default);
    Task UpdateProfileAsync(int operatorId, string displayName, string role, bool isActive, CancellationToken cancellationToken = default);
    Task UpdatePasswordAsync(int operatorId, string hash, string salt, int iterations, CancellationToken cancellationToken = default);
    Task RecordLoginFailureAsync(int operatorId, int failedCount, DateTime? lockoutUntilUtc, CancellationToken cancellationToken = default);
    Task RecordLoginSuccessAsync(int operatorId, CancellationToken cancellationToken = default);
    Task UpdateSupervisorPinAsync(
        int operatorId,
        string pinHash,
        string pinSalt,
        int iterations,
        CancellationToken cancellationToken = default);
}

public sealed class OperatorRepository : IOperatorRepository
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public OperatorRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<OperatorAccount?> GetByUsernameAsync(string username, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT OperatorId, Username, DisplayName, Role, PasswordHash, PasswordSalt, PasswordIterations,
                   IsActive, FailedLoginCount, LockoutUntilUtc, CreatedAtUtc, LastLoginAtUtc,
                   SupervisorPinHash, SupervisorPinSalt, SupervisorPinIterations
            FROM dbo.Operators
            WHERE Username = @Username;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<OperatorAccount>(
            new CommandDefinition(sql, new { Username = username }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<OperatorAccount?> GetByIdAsync(int operatorId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT OperatorId, Username, DisplayName, Role, PasswordHash, PasswordSalt, PasswordIterations,
                   IsActive, FailedLoginCount, LockoutUntilUtc, CreatedAtUtc, LastLoginAtUtc,
                   SupervisorPinHash, SupervisorPinSalt, SupervisorPinIterations
            FROM dbo.Operators
            WHERE OperatorId = @OperatorId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<OperatorAccount>(
            new CommandDefinition(sql, new { OperatorId = operatorId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OperatorAccount>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT OperatorId, Username, DisplayName, Role, PasswordHash, PasswordSalt, PasswordIterations,
                   IsActive, FailedLoginCount, LockoutUntilUtc, CreatedAtUtc, LastLoginAtUtc,
                   SupervisorPinHash, SupervisorPinSalt, SupervisorPinIterations
            FROM dbo.Operators
            ORDER BY Username;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var rows = await connection.QueryAsync<OperatorAccount>(
            new CommandDefinition(sql, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT COUNT(*) FROM dbo.Operators;", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<int> CreateAsync(OperatorAccount account, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO dbo.Operators
                (Username, DisplayName, Role, PasswordHash, PasswordSalt, PasswordIterations, IsActive)
            OUTPUT INSERTED.OperatorId
            VALUES
                (@Username, @DisplayName, @Role, @PasswordHash, @PasswordSalt, @PasswordIterations, @IsActive);
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, account, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateProfileAsync(
        int operatorId,
        string displayName,
        string role,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.Operators
            SET DisplayName = @DisplayName,
                Role = @Role,
                IsActive = @IsActive
            WHERE OperatorId = @OperatorId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { OperatorId = operatorId, DisplayName = displayName, Role = role, IsActive = isActive },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdatePasswordAsync(
        int operatorId,
        string hash,
        string salt,
        int iterations,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.Operators
            SET PasswordHash = @Hash,
                PasswordSalt = @Salt,
                PasswordIterations = @Iterations,
                FailedLoginCount = 0,
                LockoutUntilUtc = NULL
            WHERE OperatorId = @OperatorId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { OperatorId = operatorId, Hash = hash, Salt = salt, Iterations = iterations },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task RecordLoginFailureAsync(
        int operatorId,
        int failedCount,
        DateTime? lockoutUntilUtc,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.Operators
            SET FailedLoginCount = @FailedCount,
                LockoutUntilUtc = @LockoutUntilUtc
            WHERE OperatorId = @OperatorId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { OperatorId = operatorId, FailedCount = failedCount, LockoutUntilUtc = lockoutUntilUtc },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task RecordLoginSuccessAsync(int operatorId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.Operators
            SET FailedLoginCount = 0,
                LockoutUntilUtc = NULL,
                LastLoginAtUtc = SYSUTCDATETIME()
            WHERE OperatorId = @OperatorId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { OperatorId = operatorId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task UpdateSupervisorPinAsync(
        int operatorId,
        string pinHash,
        string pinSalt,
        int iterations,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE dbo.Operators
            SET SupervisorPinHash = @PinHash,
                SupervisorPinSalt = @PinSalt,
                SupervisorPinIterations = @Iterations
            WHERE OperatorId = @OperatorId;
            """;

        await using var connection = await _connectionFactory.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { OperatorId = operatorId, PinHash = pinHash, PinSalt = pinSalt, Iterations = iterations },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
