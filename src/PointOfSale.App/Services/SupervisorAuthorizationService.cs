using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Compliance;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

/// <summary>Restricted cashier actions that require supervisor elevation.</summary>
public static class SupervisorOverrideActions
{
    public const string ItemVoid = "ItemVoid";
    public const string CashDrawerOverride = "CashDrawerOverride";
    public const string PostShiftReturn = "PostShiftReturn";
    public const string DiscountLimitException = "DiscountLimitException";
    public const string PriceOverride = "PriceOverride";
}

public sealed class SupervisorOverrideRequest
{
    public required string ActionType { get; init; }
    public required string RequiredPermission { get; init; }
    public string? Reason { get; init; }
    public string? Detail { get; init; }

    /// <summary>Optional supervisor username (password path). Leave empty for PIN-only lookup.</summary>
    public string? SupervisorUsername { get; init; }

    /// <summary>Supervisor password or dedicated override PIN.</summary>
    public string? Credential { get; init; }

    /// <summary>When true, skip credential check if the signed-in operator already holds the permission.</summary>
    public bool AllowCurrentSession { get; init; } = true;
}

public sealed class SupervisorAuthorizationResult
{
    public bool Authorized { get; init; }
    public string? Error { get; init; }
    public string? AuthorizingUsername { get; init; }
    public int? AuthorizingOperatorId { get; init; }
    public string? AuthorizationMode { get; init; }
    public string? CorrelationId { get; init; }
    public string? Message { get; init; }

    public static SupervisorAuthorizationResult Denied(string error, string? correlationId = null) =>
        new()
        {
            Authorized = false,
            Error = error,
            CorrelationId = correlationId
        };

    public static SupervisorAuthorizationResult Granted(
        string username,
        int operatorId,
        string mode,
        string correlationId,
        string? message = null) =>
        new()
        {
            Authorized = true,
            AuthorizingUsername = username,
            AuthorizingOperatorId = operatorId,
            AuthorizationMode = mode,
            CorrelationId = correlationId,
            Message = message
        };
}

public interface ISupervisorAuthorizationService
{
    /// <summary>
    /// Validates session permission and/or supervisor credentials/PIN, then appends
    /// security + cryptographic compliance audit entries.
    /// </summary>
    Task<SupervisorAuthorizationResult> AuthorizeAsync(
        SupervisorOverrideRequest request,
        CancellationToken cancellationToken = default);

    Task<SupervisorAuthorizationResult> SetSupervisorPinAsync(
        int operatorId,
        string pin,
        CancellationToken cancellationToken = default);

    string ResolveRequiredPermission(string actionType);
}

public sealed class SupervisorAuthorizationOptions
{
    public const string SectionName = "SupervisorAuthorization";

    public int MinimumPinLength { get; set; } = 4;
    public int MaximumPinLength { get; set; } = 8;
    public string DefaultAdminPin { get; set; } = "2468";
}

/// <summary>
/// Supervisor PIN / credential gate for voids, drawer overrides, post-shift returns,
/// and discount-limit exceptions — with dual audit trail logging.
/// </summary>
public sealed class SupervisorAuthorizationService : ISupervisorAuthorizationService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IAuditSecurityLogger _securityAudit;
    private readonly IComplianceAuditLogger _complianceAudit;
    private readonly SupervisorAuthorizationOptions _options;
    private readonly ILogger<SupervisorAuthorizationService> _logger;

    public SupervisorAuthorizationService(
        IServiceScopeFactory scopeFactory,
        IAuthenticationAuthorizationService auth,
        IPasswordHasher passwordHasher,
        IAuditSecurityLogger securityAudit,
        IComplianceAuditLogger complianceAudit,
        IOptions<SupervisorAuthorizationOptions> options,
        ILogger<SupervisorAuthorizationService> logger)
    {
        _scopeFactory = scopeFactory;
        _auth = auth;
        _passwordHasher = passwordHasher;
        _securityAudit = securityAudit;
        _complianceAudit = complianceAudit;
        _options = options.Value;
        _logger = logger;
    }

    public string ResolveRequiredPermission(string actionType) =>
        actionType switch
        {
            SupervisorOverrideActions.ItemVoid => OperatorPermissions.PerformVoid,
            SupervisorOverrideActions.PostShiftReturn => OperatorPermissions.PerformVoid,
            SupervisorOverrideActions.CashDrawerOverride => OperatorPermissions.OpenCashDrawer,
            SupervisorOverrideActions.DiscountLimitException => OperatorPermissions.ApplyCartDiscount,
            SupervisorOverrideActions.PriceOverride => OperatorPermissions.OverridePrice,
            _ => OperatorPermissions.PerformVoid
        };

    public async Task<SupervisorAuthorizationResult> AuthorizeAsync(
        SupervisorOverrideRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ActionType);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RequiredPermission);

        var correlationId = Guid.NewGuid().ToString("N");
        var cashier = _auth.CurrentOperator?.Username ?? "(anonymous)";

        if (request.AllowCurrentSession && _auth.HasPermission(request.RequiredPermission))
        {
            var session = _auth.CurrentOperator!;
            var granted = SupervisorAuthorizationResult.Granted(
                session.Username,
                session.OperatorId,
                "SessionPermission",
                correlationId,
                $"Session operator '{session.Username}' authorized {request.ActionType}.");
            await WriteAuditAsync(request, granted, cashier, cancellationToken).ConfigureAwait(false);
            return granted;
        }

        var credential = request.Credential?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(credential))
        {
            var denied = SupervisorAuthorizationResult.Denied(
                "Supervisor credentials or PIN are required for this restricted action.",
                correlationId);
            await WriteAuditAsync(request, denied, cashier, cancellationToken).ConfigureAwait(false);
            return denied;
        }

        using var scope = _scopeFactory.CreateScope();
        var operators = scope.ServiceProvider.GetRequiredService<IOperatorRepository>();

        OperatorAccount? authorizer = null;
        string mode;

        if (!string.IsNullOrWhiteSpace(request.SupervisorUsername))
        {
            authorizer = await operators
                .GetByUsernameAsync(request.SupervisorUsername.Trim(), cancellationToken)
                .ConfigureAwait(false);
            if (authorizer is null || !authorizer.IsActive)
            {
                var denied = SupervisorAuthorizationResult.Denied("Supervisor account not found or inactive.", correlationId);
                await WriteAuditAsync(request, denied, cashier, cancellationToken).ConfigureAwait(false);
                return denied;
            }

            if (!RolePermissionCatalog.GetPermissions(authorizer.Role).Contains(request.RequiredPermission))
            {
                var denied = SupervisorAuthorizationResult.Denied(
                    $"Supervisor role '{authorizer.Role}' lacks permission '{request.RequiredPermission}'.",
                    correlationId);
                await WriteAuditAsync(request, denied, cashier, cancellationToken).ConfigureAwait(false);
                return denied;
            }

            if (TryVerifyPin(authorizer, credential))
            {
                mode = "SupervisorPin";
            }
            else if (_passwordHasher.VerifyPassword(
                         credential,
                         authorizer.PasswordHash,
                         authorizer.PasswordSalt,
                         authorizer.PasswordIterations))
            {
                mode = "SupervisorPassword";
            }
            else
            {
                var denied = SupervisorAuthorizationResult.Denied("Invalid supervisor password or PIN.", correlationId);
                await WriteAuditAsync(request, denied, cashier, cancellationToken).ConfigureAwait(false);
                return denied;
            }
        }
        else
        {
            // PIN-only: match against any active elevated operator with a configured PIN.
            var candidates = await operators.GetAllAsync(cancellationToken).ConfigureAwait(false);
            authorizer = candidates.FirstOrDefault(op =>
                op.IsActive
                && RolePermissionCatalog.GetPermissions(op.Role).Contains(request.RequiredPermission)
                && TryVerifyPin(op, credential));

            if (authorizer is null)
            {
                var denied = SupervisorAuthorizationResult.Denied(
                    "Supervisor PIN rejected — no matching elevated operator PIN.",
                    correlationId);
                await WriteAuditAsync(request, denied, cashier, cancellationToken).ConfigureAwait(false);
                return denied;
            }

            mode = "SupervisorPin";
        }

        var result = SupervisorAuthorizationResult.Granted(
            authorizer.Username,
            authorizer.OperatorId,
            mode,
            correlationId,
            $"Authorized {request.ActionType} via {mode} by '{authorizer.Username}'.");
        await WriteAuditAsync(request, result, cashier, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Supervisor override granted: action={Action} by={User} mode={Mode} correlation={Correlation}",
            request.ActionType,
            authorizer.Username,
            mode,
            correlationId);
        return result;
    }

    public async Task<SupervisorAuthorizationResult> SetSupervisorPinAsync(
        int operatorId,
        string pin,
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.ManageUsers);

        var min = Math.Max(4, _options.MinimumPinLength);
        var max = Math.Max(min, _options.MaximumPinLength);
        if (string.IsNullOrWhiteSpace(pin) || pin.Length < min || pin.Length > max || !pin.All(char.IsDigit))
        {
            return SupervisorAuthorizationResult.Denied(
                $"Supervisor PIN must be {min}-{max} digits.");
        }

        using var scope = _scopeFactory.CreateScope();
        var operators = scope.ServiceProvider.GetRequiredService<IOperatorRepository>();
        var account = await operators.GetByIdAsync(operatorId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return SupervisorAuthorizationResult.Denied("Operator not found.");
        }

        var (hash, salt, iterations) = _passwordHasher.HashPassword(pin);
        await operators.UpdateSupervisorPinAsync(operatorId, hash, salt, iterations, cancellationToken)
            .ConfigureAwait(false);

        return SupervisorAuthorizationResult.Granted(
            account.Username,
            operatorId,
            "PinProvisioned",
            Guid.NewGuid().ToString("N"),
            "Supervisor PIN updated.");
    }

    private bool TryVerifyPin(OperatorAccount account, string credential)
    {
        if (string.IsNullOrWhiteSpace(account.SupervisorPinHash)
            || string.IsNullOrWhiteSpace(account.SupervisorPinSalt)
            || account.SupervisorPinIterations < 10_000)
        {
            return false;
        }

        var min = Math.Max(4, _options.MinimumPinLength);
        var max = Math.Max(min, _options.MaximumPinLength);
        if (credential.Length < min || credential.Length > max || !credential.All(char.IsDigit))
        {
            return false;
        }

        return _passwordHasher.VerifyPassword(
            credential,
            account.SupervisorPinHash,
            account.SupervisorPinSalt,
            account.SupervisorPinIterations);
    }

    private async Task WriteAuditAsync(
        SupervisorOverrideRequest request,
        SupervisorAuthorizationResult result,
        string cashierUsername,
        CancellationToken cancellationToken)
    {
        var detail =
            $"action={request.ActionType}; permission={request.RequiredPermission}; " +
            $"cashier={cashierUsername}; authorizer={result.AuthorizingUsername ?? "-"}; " +
            $"mode={result.AuthorizationMode ?? "-"}; reason={request.Reason ?? "-"}; " +
            $"detail={request.Detail ?? "-"}; correlation={result.CorrelationId}";

        try
        {
            await _securityAudit.LogAsync(
                    SecurityAuditActions.SupervisorOverride,
                    detail: detail,
                    success: result.Authorized,
                    operatorId: result.AuthorizingOperatorId ?? _auth.CurrentOperator?.OperatorId,
                    username: result.AuthorizingUsername ?? cashierUsername,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write security audit for supervisor override.");
        }

        try
        {
            await _complianceAudit.LogEventAsync(
                    ComplianceAuditCategories.SupervisorAuth,
                    action: request.ActionType,
                    detail: detail,
                    success: result.Authorized,
                    correlationId: result.CorrelationId,
                    operatorUsername: result.AuthorizingUsername ?? cashierUsername,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write compliance audit for supervisor override.");
        }
    }
}
