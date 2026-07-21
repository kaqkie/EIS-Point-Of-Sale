using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Data;

namespace PointOfSale.App.Services;

public static class SecurityAuditActions
{
    public const string SignIn = "SignIn";
    public const string SignOut = "SignOut";
    public const string SignInFailed = "SignInFailed";
    public const string CreateUser = "CreateUser";
    public const string UpdateUser = "UpdateUser";
    public const string ResetPassword = "ResetPassword";
    public const string AdminOverride = "AdminOverride";
    public const string DrawerOpen = "DrawerOpen";
    public const string QueueIntervention = "QueueIntervention";
    public const string BackupTriggered = "BackupTriggered";
    public const string PermissionDenied = "PermissionDenied";
}

public interface IAuditSecurityLogger
{
    Task LogAsync(
        string action,
        string? detail = null,
        bool success = true,
        int? operatorId = null,
        string? username = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SecurityAuditEntry>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default);
}

public sealed class SecurityAuditEntry
{
    public long AuditId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public int? OperatorId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// Append-only security audit writer for operator sign-ins, overrides, and sensitive interventions.
/// </summary>
public sealed class AuditSecurityLogger : IAuditSecurityLogger
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditSecurityLogger> _logger;

    public AuditSecurityLogger(IServiceScopeFactory scopeFactory, ILogger<AuditSecurityLogger> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task LogAsync(
        string action,
        string? detail = null,
        bool success = true,
        int? operatorId = null,
        string? username = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var connections = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();
            await using var connection = await connections.CreateOpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);

            const string sql = """
                INSERT INTO dbo.SecurityAuditLog (OperatorId, Username, Action, Detail, Success)
                VALUES (@OperatorId, @Username, @Action, @Detail, @Success);
                """;

            await connection.ExecuteAsync(
                new CommandDefinition(
                    sql,
                    new
                    {
                        OperatorId = operatorId,
                        Username = Truncate(username ?? string.Empty, 64),
                        Action = Truncate(action, 80),
                        Detail = Truncate(detail, 2000),
                        Success = success
                    },
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Security audit must not crash POS operations.
            _logger.LogWarning(ex, "Failed to write security audit action {Action}.", action);
        }
    }

    public async Task<IReadOnlyList<SecurityAuditEntry>> GetRecentAsync(
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<ISqlConnectionFactory>();
        await using var connection = await connections.CreateOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        const string sql = """
            SELECT TOP (@Take)
                AuditId, CreatedAtUtc, OperatorId, Username, Action, Detail, Success
            FROM dbo.SecurityAuditLog
            ORDER BY CreatedAtUtc DESC, AuditId DESC;
            """;

        var rows = await connection.QueryAsync<SecurityAuditEntry>(
            new CommandDefinition(sql, new { Take = take }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return rows.AsList();
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
