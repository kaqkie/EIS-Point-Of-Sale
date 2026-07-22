using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Database.Seeders;

/// <summary>
/// First-boot seeder for SQL Express operator directory.
/// Provisions hashed admin/cashier accounts with RolePermissionCatalog boundaries.
/// </summary>
public interface IInitialDataSeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}

public sealed class InitialDataSeeder : IInitialDataSeeder
{
    public const string DefaultAdminUsername = "admin";
    public const string DefaultAdminPassword = "admin123";
    public const string DefaultAdminDisplayName = "Store Administrator";

    public const string DefaultCashierUsername = "cashier";
    public const string DefaultCashierPassword = "cashier123";
    public const string DefaultCashierDisplayName = "Front Counter Cashier";

    private readonly IOperatorRepository _operators;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditSecurityLogger _auditLogger;
    private readonly AuthenticationOptions _options;
    private readonly ILogger<InitialDataSeeder> _logger;

    public InitialDataSeeder(
        IOperatorRepository operators,
        IPasswordHasher passwordHasher,
        IAuditSecurityLogger auditLogger,
        IOptions<AuthenticationOptions> options,
        ILogger<InitialDataSeeder> logger)
    {
        _operators = operators;
        _passwordHasher = passwordHasher;
        _auditLogger = auditLogger;
        _options = options.Value;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        var adminUser = string.IsNullOrWhiteSpace(_options.DefaultAdminUsername)
            ? DefaultAdminUsername
            : _options.DefaultAdminUsername.Trim().ToLowerInvariant();
        var adminPassword = string.IsNullOrWhiteSpace(_options.DefaultAdminPassword)
            ? DefaultAdminPassword
            : _options.DefaultAdminPassword;
        var adminDisplay = string.IsNullOrWhiteSpace(_options.DefaultAdminDisplayName)
            ? DefaultAdminDisplayName
            : _options.DefaultAdminDisplayName.Trim();

        var cashierUser = string.IsNullOrWhiteSpace(_options.DefaultCashierUsername)
            ? DefaultCashierUsername
            : _options.DefaultCashierUsername.Trim().ToLowerInvariant();
        var cashierPassword = string.IsNullOrWhiteSpace(_options.DefaultCashierPassword)
            ? DefaultCashierPassword
            : _options.DefaultCashierPassword;
        var cashierDisplay = string.IsNullOrWhiteSpace(_options.DefaultCashierDisplayName)
            ? DefaultCashierDisplayName
            : _options.DefaultCashierDisplayName.Trim();

        await EnsureOperatorAsync(
                adminUser,
                adminDisplay,
                OperatorRoles.Administrator,
                adminPassword,
                cancellationToken)
            .ConfigureAwait(false);

        await EnsureOperatorAsync(
                cashierUser,
                cashierDisplay,
                OperatorRoles.Cashier,
                cashierPassword,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsureOperatorAsync(
        string username,
        string displayName,
        string role,
        string password,
        CancellationToken cancellationToken)
    {
        var existing = await _operators.GetByUsernameAsync(username, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return;
        }

        var (hash, salt, iterations) = _passwordHasher.HashPassword(password);
        await _operators.CreateAsync(
                new OperatorAccount
                {
                    Username = username,
                    DisplayName = displayName,
                    Role = role,
                    PasswordHash = hash,
                    PasswordSalt = salt,
                    PasswordIterations = iterations,
                    IsActive = true
                },
                cancellationToken)
            .ConfigureAwait(false);

        await _auditLogger.LogAsync(
                SecurityAuditActions.CreateUser,
                detail: $"Seeded default {role} operator '{username}' (PBKDF2).",
                success: true,
                username: "system",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogWarning(
            "Seeded default {Role} account '{Username}'. Change the password after first login.",
            role,
            username);
    }
}
