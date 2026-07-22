using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface IAuthenticationAuthorizationService
{
    event EventHandler? SessionChanged;

    bool IsAuthenticated { get; }
    OperatorSession? CurrentOperator { get; }

    bool HasPermission(string permission);
    void EnsurePermission(string permission);

    Task EnsureSeededAsync(CancellationToken cancellationToken = default);
    Task<AuthResult> SignInAsync(string username, string password, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OperatorAccount>> GetOperatorsAsync(CancellationToken cancellationToken = default);
    Task<AuthResult> CreateOperatorAsync(
        string username,
        string displayName,
        string role,
        string password,
        CancellationToken cancellationToken = default);
    Task<AuthResult> UpdateOperatorAsync(
        int operatorId,
        string displayName,
        string role,
        bool isActive,
        CancellationToken cancellationToken = default);
    Task<AuthResult> ResetPasswordAsync(
        int operatorId,
        string newPassword,
        CancellationToken cancellationToken = default);

    /// <summary>Self-service password change for the signed-in operator.</summary>
    Task<AuthResult> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default);
}

public sealed class AuthResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public OperatorSession? Session { get; init; }

    public static AuthResult Fail(string error) => new() { Success = false, Error = error };
    public static AuthResult Ok(OperatorSession? session = null) => new() { Success = true, Session = session };
}

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    public int MaxFailedLogins { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public string DefaultAdminUsername { get; set; } = "admin";
    public string DefaultAdminPassword { get; set; } = "ChangeMe!123";
    public string DefaultAdminDisplayName { get; set; } = "Store Administrator";
}

/// <summary>
/// Operator authentication (PBKDF2) and role-based authorization for Albert Retail Terminal.
/// </summary>
public sealed class AuthenticationAuthorizationService : IAuthenticationAuthorizationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditSecurityLogger _auditLogger;
    private readonly AuthenticationOptions _options;
    private readonly ILogger<AuthenticationAuthorizationService> _logger;
    private readonly object _gate = new();

    private OperatorSession? _current;

    public AuthenticationAuthorizationService(
        IServiceScopeFactory scopeFactory,
        IPasswordHasher passwordHasher,
        IAuditSecurityLogger auditLogger,
        IOptions<AuthenticationOptions> options,
        ILogger<AuthenticationAuthorizationService> logger)
    {
        _scopeFactory = scopeFactory;
        _passwordHasher = passwordHasher;
        _auditLogger = auditLogger;
        _options = options.Value;
        _logger = logger;
    }

    public event EventHandler? SessionChanged;

    public bool IsAuthenticated => _current is not null;
    public OperatorSession? CurrentOperator => _current;

    public bool HasPermission(string permission)
    {
        var session = _current;
        return session is not null && session.Permissions.Contains(permission);
    }

    public void EnsurePermission(string permission)
    {
        if (HasPermission(permission))
        {
            return;
        }

        var username = _current?.Username ?? "(anonymous)";
        _ = _auditLogger.LogAsync(
            SecurityAuditActions.PermissionDenied,
            detail: permission,
            success: false,
            operatorId: _current?.OperatorId,
            username: username);

        throw new UnauthorizedAccessException($"Permission '{permission}' is required.");
    }

    public async Task EnsureSeededAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOperatorRepository>();
        var count = await repo.CountAsync(cancellationToken).ConfigureAwait(false);
        if (count > 0)
        {
            return;
        }

        var (hash, salt, iterations) = _passwordHasher.HashPassword(_options.DefaultAdminPassword);
        await repo.CreateAsync(
                new OperatorAccount
                {
                    Username = _options.DefaultAdminUsername.Trim().ToLowerInvariant(),
                    DisplayName = _options.DefaultAdminDisplayName,
                    Role = OperatorRoles.Administrator,
                    PasswordHash = hash,
                    PasswordSalt = salt,
                    PasswordIterations = iterations,
                    IsActive = true
                },
                cancellationToken)
            .ConfigureAwait(false);

        await _auditLogger.LogAsync(
                SecurityAuditActions.CreateUser,
                detail: $"Seeded default administrator '{_options.DefaultAdminUsername}'.",
                success: true,
                username: "system",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        _logger.LogWarning(
            "Seeded default administrator '{Username}'. Change the password immediately after first login.",
            _options.DefaultAdminUsername);
    }

    public async Task<AuthResult> SignInAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return AuthResult.Fail("Username and password are required.");
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOperatorRepository>();
        var normalized = username.Trim().ToLowerInvariant();
        var account = await repo.GetByUsernameAsync(normalized, cancellationToken).ConfigureAwait(false);
        if (account is null || !account.IsActive)
        {
            await _auditLogger.LogAsync(
                    SecurityAuditActions.SignInFailed,
                    detail: $"Unknown or inactive user '{normalized}'.",
                    success: false,
                    username: normalized,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return AuthResult.Fail("Invalid username or password.");
        }

        if (account.LockoutUntilUtc is { } lockout && lockout > DateTime.UtcNow)
        {
            await _auditLogger.LogAsync(
                    SecurityAuditActions.SignInFailed,
                    detail: $"Account locked until {lockout:u}.",
                    success: false,
                    operatorId: account.OperatorId,
                    username: account.Username,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return AuthResult.Fail($"Account locked until {lockout.ToLocalTime():g}.");
        }

        if (!_passwordHasher.VerifyPassword(
                password,
                account.PasswordHash,
                account.PasswordSalt,
                account.PasswordIterations))
        {
            var failures = account.FailedLoginCount + 1;
            DateTime? until = null;
            if (failures >= Math.Max(3, _options.MaxFailedLogins))
            {
                until = DateTime.UtcNow.AddMinutes(Math.Max(1, _options.LockoutMinutes));
                failures = 0;
            }

            await repo.RecordLoginFailureAsync(account.OperatorId, failures, until, cancellationToken)
                .ConfigureAwait(false);
            await _auditLogger.LogAsync(
                    SecurityAuditActions.SignInFailed,
                    detail: "Invalid password.",
                    success: false,
                    operatorId: account.OperatorId,
                    username: account.Username,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return AuthResult.Fail(until is null
                ? "Invalid username or password."
                : $"Too many failed attempts. Account locked until {until.Value.ToLocalTime():g}.");
        }

        await repo.RecordLoginSuccessAsync(account.OperatorId, cancellationToken).ConfigureAwait(false);
        var session = ToSession(account);
        lock (_gate)
        {
            _current = session;
        }

        await _auditLogger.LogAsync(
                SecurityAuditActions.SignIn,
                detail: $"Role={account.Role}",
                success: true,
                operatorId: account.OperatorId,
                username: account.Username,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        SessionChanged?.Invoke(this, EventArgs.Empty);
        return AuthResult.Ok(session);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        OperatorSession? previous;
        lock (_gate)
        {
            previous = _current;
            _current = null;
        }

        if (previous is not null)
        {
            await _auditLogger.LogAsync(
                    SecurityAuditActions.SignOut,
                    success: true,
                    operatorId: previous.OperatorId,
                    username: previous.Username,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<OperatorAccount>> GetOperatorsAsync(CancellationToken cancellationToken = default)
    {
        EnsurePermission(OperatorPermissions.ManageUsers);
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOperatorRepository>();
        return await repo.GetAllAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthResult> CreateOperatorAsync(
        string username,
        string displayName,
        string role,
        string password,
        CancellationToken cancellationToken = default)
    {
        EnsurePermission(OperatorPermissions.ManageUsers);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            return AuthResult.Fail("Username and password are required.");
        }

        if (!OperatorRoles.All.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return AuthResult.Fail("Invalid role.");
        }

        if (password.Length < 8)
        {
            return AuthResult.Fail("Password must be at least 8 characters.");
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOperatorRepository>();
        var normalized = username.Trim().ToLowerInvariant();
        if (await repo.GetByUsernameAsync(normalized, cancellationToken).ConfigureAwait(false) is not null)
        {
            return AuthResult.Fail("Username already exists.");
        }

        var (hash, salt, iterations) = _passwordHasher.HashPassword(password);
        var id = await repo.CreateAsync(
                new OperatorAccount
                {
                    Username = normalized,
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? normalized : displayName.Trim(),
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
                detail: $"Created operatorId={id}, role={role}",
                success: true,
                operatorId: _current?.OperatorId,
                username: _current?.Username,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return AuthResult.Ok();
    }

    public async Task<AuthResult> UpdateOperatorAsync(
        int operatorId,
        string displayName,
        string role,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        EnsurePermission(OperatorPermissions.ManageUsers);
        if (!OperatorRoles.All.Contains(role, StringComparer.OrdinalIgnoreCase))
        {
            return AuthResult.Fail("Invalid role.");
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOperatorRepository>();
        var existing = await repo.GetByIdAsync(operatorId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return AuthResult.Fail("Operator not found.");
        }

        if (!isActive && existing.Role == OperatorRoles.Administrator)
        {
            var all = await repo.GetAllAsync(cancellationToken).ConfigureAwait(false);
            if (all.Count(o => o.IsActive && o.Role == OperatorRoles.Administrator) <= 1)
            {
                return AuthResult.Fail("Cannot deactivate the last active administrator.");
            }
        }

        await repo.UpdateProfileAsync(operatorId, displayName.Trim(), role, isActive, cancellationToken)
            .ConfigureAwait(false);
        await _auditLogger.LogAsync(
                SecurityAuditActions.UpdateUser,
                detail: $"operatorId={operatorId}, role={role}, active={isActive}",
                success: true,
                operatorId: _current?.OperatorId,
                username: _current?.Username,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return AuthResult.Ok();
    }

    public async Task<AuthResult> ResetPasswordAsync(
        int operatorId,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        EnsurePermission(OperatorPermissions.ManageUsers);
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            return AuthResult.Fail("Password must be at least 8 characters.");
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOperatorRepository>();
        if (await repo.GetByIdAsync(operatorId, cancellationToken).ConfigureAwait(false) is null)
        {
            return AuthResult.Fail("Operator not found.");
        }

        var (hash, salt, iterations) = _passwordHasher.HashPassword(newPassword);
        await repo.UpdatePasswordAsync(operatorId, hash, salt, iterations, cancellationToken).ConfigureAwait(false);
        await _auditLogger.LogAsync(
                SecurityAuditActions.ResetPassword,
                detail: $"operatorId={operatorId}",
                success: true,
                operatorId: _current?.OperatorId,
                username: _current?.Username,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return AuthResult.Ok();
    }

    public async Task<AuthResult> ChangeOwnPasswordAsync(
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var session = _current
            ?? throw new UnauthorizedAccessException("Sign in before changing your password.");

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            return AuthResult.Fail("Password must be at least 8 characters.");
        }

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOperatorRepository>();
        var account = await repo.GetByIdAsync(session.OperatorId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return AuthResult.Fail("Operator account not found.");
        }

        if (!_passwordHasher.VerifyPassword(currentPassword, account.PasswordHash, account.PasswordSalt, account.PasswordIterations))
        {
            await _auditLogger.LogAsync(
                    SecurityAuditActions.ResetPassword,
                    detail: "self-service change rejected — current password mismatch",
                    success: false,
                    operatorId: session.OperatorId,
                    username: session.Username,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return AuthResult.Fail("Current password is incorrect.");
        }

        var (hash, salt, iterations) = _passwordHasher.HashPassword(newPassword);
        await repo.UpdatePasswordAsync(session.OperatorId, hash, salt, iterations, cancellationToken)
            .ConfigureAwait(false);
        await _auditLogger.LogAsync(
                SecurityAuditActions.ResetPassword,
                detail: "self-service password change",
                success: true,
                operatorId: session.OperatorId,
                username: session.Username,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return AuthResult.Ok();
    }

    private static OperatorSession ToSession(OperatorAccount account) =>
        new()
        {
            OperatorId = account.OperatorId,
            Username = account.Username,
            DisplayName = account.DisplayName,
            Role = account.Role,
            Permissions = RolePermissionCatalog.GetPermissions(account.Role),
            SignedInAtUtc = DateTime.UtcNow
        };
}
