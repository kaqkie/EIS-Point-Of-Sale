using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Deployment;
using PointOfSale.App.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Models;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Security;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Serialization;
using InfraTerminalActivationResult = PointOfSale.Infrastructure.Services.TerminalActivationResult;

namespace PointOfSale.App.Services;

public interface IMraOnboardingService
{
    /// <summary>POST /api/v1/onboarding/activate-terminal — bind POS terminal to MRA gateway.</summary>
    Task<MraOnboardingResult> ActivateTerminalAsync(
        string activationKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// POST /api/v1/onboarding/terminal-activated-confirmation — confirm processing and persist Activated state.
    /// </summary>
    Task<MraOnboardingResult> ConfirmTerminalActivatedAsync(
        string terminalId,
        string activationKey,
        CancellationToken cancellationToken = default);

    /// <summary>Full activate → confirm → encrypted credential persistence pipeline.</summary>
    Task<MraOnboardingResult> ActivateAndConfirmAsync(
        string activationKey,
        string? branchId = null,
        CancellationToken cancellationToken = default);
}

public sealed class MraOnboardingResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? TerminalId { get; init; }
    public bool UsedSandboxLocalFallback { get; init; }

    /// <summary>Upstream HTTP status when the EIS endpoint returned a non-success transport result.</summary>
    public int? UpstreamHttpStatus { get; init; }

    /// <summary>Truncated upstream body / remark captured for diagnostics (never contains raw secrets).</summary>
    public string? UpstreamDiagnostic { get; init; }

    public static MraOnboardingResult Ok(
        string message,
        string? terminalId = null,
        bool sandboxFallback = false,
        int? upstreamHttpStatus = null,
        string? upstreamDiagnostic = null) =>
        new()
        {
            Success = true,
            Message = message,
            TerminalId = terminalId,
            UsedSandboxLocalFallback = sandboxFallback,
            UpstreamHttpStatus = upstreamHttpStatus,
            UpstreamDiagnostic = upstreamDiagnostic
        };

    public static MraOnboardingResult Fail(
        string message,
        int? upstreamHttpStatus = null,
        string? upstreamDiagnostic = null) =>
        new()
        {
            Success = false,
            Message = message,
            UpstreamHttpStatus = upstreamHttpStatus,
            UpstreamDiagnostic = upstreamDiagnostic
        };
}

/// <summary>
/// Phase 40 — App-layer facade over official MRA EIS onboarding endpoints
/// (<c>onboarding/activate-terminal</c>, <c>onboarding/terminal-activated-confirmation</c>).
/// Handles sandbox/mock HTTP errors gracefully, persists JWT / secret keys via DPAPI into SQL Express,
/// and marks the terminal Activated.
/// </summary>
public sealed class MraOnboardingService : IMraOnboardingService
{
    public const string ActivateTerminalPath = "onboarding/activate-terminal";
    public const string TerminalActivatedConfirmationPath = "onboarding/terminal-activated-confirmation";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITerminalActivationService _licenseActivation;
    private readonly TerminalDeploymentOptions _deployment;
    private readonly MraApiOptions _mraOptions;
    private readonly ILogger<MraOnboardingService> _logger;

    public MraOnboardingService(
        IServiceScopeFactory scopeFactory,
        ITerminalActivationService licenseActivation,
        IOptions<TerminalDeploymentOptions> deployment,
        IOptions<MraApiOptions> mraOptions,
        ILogger<MraOnboardingService> logger)
    {
        _scopeFactory = scopeFactory;
        _licenseActivation = licenseActivation;
        _deployment = deployment.Value;
        _mraOptions = mraOptions.Value;
        _logger = logger;
    }

    public async Task<MraOnboardingResult> ActivateTerminalAsync(
        string activationKey,
        CancellationToken cancellationToken = default)
    {
        if (!_licenseActivation.ValidateLicenseKeyFormat(activationKey, out var normalized, out var formatError))
        {
            return MraOnboardingResult.Fail(formatError ?? "Invalid activation key format.");
        }

        if (!_licenseActivation.AcceptsLicenseKey(normalized))
        {
            return MraOnboardingResult.Fail(
                "Activation key is not valid. Check the key and try again (format I4CV-M5YY-AKY6-Z9BT).");
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var onboarding = scope.ServiceProvider.GetRequiredService<TerminalOnboardingService>();
            var request = await BuildActivationRequestAsync(scope, normalized, cancellationToken)
                .ConfigureAwait(false);

            var result = await onboarding.ActivateTerminalAsync(request, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success || string.IsNullOrWhiteSpace(result.TerminalId))
            {
                LogActivationEndpointRejection(result.Remark, result.Errors);
                return await TrySandboxFallbackOrFailAsync(
                        scope,
                        normalized,
                        result.Remark ?? "MRA activate-terminal rejected the activation key.",
                        confirm: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _logger.LogInformation(
                "MRA activate-terminal succeeded for terminal {TerminalId}. Credentials staged pending confirmation.",
                result.TerminalId);

            return MraOnboardingResult.Ok(
                result.Remark ?? $"Terminal {result.TerminalId} pending MRA confirmation.",
                result.TerminalId);
        }
        catch (Exception ex) when (IsRecoverableMraEndpointFailure(ex))
        {
            LogRecoverableEndpointFailure(ex, "activate-terminal");
            using var scope = _scopeFactory.CreateScope();
            return await TrySandboxFallbackOrFailAsync(
                    scope,
                    normalized,
                    BuildUpstreamMessage(ex),
                    confirm: false,
                    cancellationToken,
                    upstreamHttpStatus: ExtractHttpStatus(ex),
                    upstreamDiagnostic: ExtractDiagnostic(ex))
                .ConfigureAwait(false);
        }
    }

    public async Task<MraOnboardingResult> ConfirmTerminalActivatedAsync(
        string terminalId,
        string activationKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(terminalId))
        {
            return MraOnboardingResult.Fail("Terminal ID is required for confirmation.");
        }

        if (!_licenseActivation.ValidateLicenseKeyFormat(activationKey, out var normalized, out var formatError))
        {
            return MraOnboardingResult.Fail(formatError ?? "Invalid activation key format.");
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var onboarding = scope.ServiceProvider.GetRequiredService<TerminalOnboardingService>();

            var result = await onboarding.ConfirmTerminalActivationAsync(
                    new TerminalActivationConfirmationRequest
                    {
                        TerminalId = terminalId.Trim(),
                        TerminalActivationCode = normalized
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.LogWarning(
                    "MRA terminal-activated-confirmation rejected for {TerminalId}: {Remark}",
                    terminalId,
                    result.Remark);
                return MraOnboardingResult.Fail(
                    result.Remark ?? "MRA terminal-activated-confirmation failed.");
            }

            await MarkOnboardingCompleteFlagAsync(scope, cancellationToken).ConfigureAwait(false);

            return MraOnboardingResult.Ok(
                result.Remark ?? $"Terminal {terminalId} confirmed with MRA.",
                terminalId.Trim());
        }
        catch (Exception ex) when (IsRecoverableMraEndpointFailure(ex))
        {
            LogRecoverableEndpointFailure(ex, "terminal-activated-confirmation");
            using var scope = _scopeFactory.CreateScope();
            return await TrySandboxFallbackOrFailAsync(
                    scope,
                    normalized,
                    BuildUpstreamMessage(ex),
                    confirm: true,
                    cancellationToken,
                    preferredTerminalId: terminalId.Trim(),
                    upstreamHttpStatus: ExtractHttpStatus(ex),
                    upstreamDiagnostic: ExtractDiagnostic(ex))
                .ConfigureAwait(false);
        }
    }

    public async Task<MraOnboardingResult> ActivateAndConfirmAsync(
        string activationKey,
        string? branchId = null,
        CancellationToken cancellationToken = default)
    {
        if (!_licenseActivation.ValidateLicenseKeyFormat(activationKey, out var normalized, out var formatError))
        {
            return MraOnboardingResult.Fail(formatError ?? "Invalid activation key format.");
        }

        if (!_licenseActivation.AcceptsLicenseKey(normalized))
        {
            return MraOnboardingResult.Fail(
                "Activation key is not valid. Check the key and try again (format I4CV-M5YY-AKY6-Z9BT).");
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var onboarding = scope.ServiceProvider.GetRequiredService<TerminalOnboardingService>();
            var request = await BuildActivationRequestAsync(scope, normalized, cancellationToken, branchId)
                .ConfigureAwait(false);

            InfraTerminalActivationResult activate;
            try
            {
                activate = await onboarding.ActivateTerminalAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableMraEndpointFailure(ex))
            {
                LogRecoverableEndpointFailure(ex, "activate-terminal");
                return await CompleteSandboxLocalOnboardingAsync(
                        scope,
                        normalized,
                        cancellationToken,
                        upstreamHttpStatus: ExtractHttpStatus(ex),
                        upstreamDiagnostic: ExtractDiagnostic(ex))
                    .ConfigureAwait(false);
            }

            if (!activate.Success || string.IsNullOrWhiteSpace(activate.TerminalId))
            {
                LogActivationEndpointRejection(activate.Remark, activate.Errors);

                // Invalid TAC from live MRA — do not silently unlock production.
                if (IsLiveProductionEnvironment())
                {
                    return MraOnboardingResult.Fail(
                        activate.Remark ?? "MRA rejected the activation key.",
                        upstreamDiagnostic: activate.Remark);
                }

                _logger.LogWarning(
                    "MRA activate-terminal returned failure ({Remark}); applying sandbox local onboarding for valid ART key.",
                    activate.Remark);
                return await CompleteSandboxLocalOnboardingAsync(
                        scope,
                        normalized,
                        cancellationToken,
                        upstreamDiagnostic: activate.Remark)
                    .ConfigureAwait(false);
            }

            _logger.LogInformation(
                "MRA activate-terminal returned terminal {TerminalId}; TerminalCredentials stored encrypted pending confirmation.",
                activate.TerminalId);

            TerminalConfirmationResult confirm;
            try
            {
                confirm = await onboarding.ConfirmTerminalActivationAsync(
                        new TerminalActivationConfirmationRequest
                        {
                            TerminalId = activate.TerminalId,
                            TerminalActivationCode = normalized
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRecoverableMraEndpointFailure(ex))
            {
                LogRecoverableEndpointFailure(ex, "terminal-activated-confirmation");
                return await CompleteSandboxLocalOnboardingAsync(
                        scope,
                        normalized,
                        cancellationToken,
                        preferredTerminalId: activate.TerminalId,
                        upstreamHttpStatus: ExtractHttpStatus(ex),
                        upstreamDiagnostic: ExtractDiagnostic(ex))
                    .ConfigureAwait(false);
            }

            if (!confirm.Success)
            {
                _logger.LogWarning(
                    "MRA confirmation failed after activate-terminal for {TerminalId}: {Remark}",
                    activate.TerminalId,
                    confirm.Remark);

                if (IsLiveProductionEnvironment())
                {
                    return MraOnboardingResult.Fail(
                        confirm.Remark ?? "MRA confirmation failed after activate-terminal.",
                        upstreamDiagnostic: confirm.Remark);
                }

                return await CompleteSandboxLocalOnboardingAsync(
                        scope,
                        normalized,
                        cancellationToken,
                        preferredTerminalId: activate.TerminalId,
                        upstreamDiagnostic: confirm.Remark)
                    .ConfigureAwait(false);
            }

            await MarkOnboardingCompleteFlagAsync(scope, cancellationToken).ConfigureAwait(false);

            return MraOnboardingResult.Ok(
                $"MRA onboarding complete for terminal {activate.TerminalId}. Credentials stored encrypted.",
                activate.TerminalId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MRA ActivateAndConfirm failed.");
            return MraOnboardingResult.Fail(
                ex.Message,
                ExtractHttpStatus(ex),
                ExtractDiagnostic(ex));
        }
    }

    private async Task<TerminalActivationRequest> BuildActivationRequestAsync(
        IServiceScope scope,
        string normalizedActivationKey,
        CancellationToken cancellationToken,
        string? branchOverride = null)
    {
        var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        var branch = !string.IsNullOrWhiteSpace(branchOverride)
            ? branchOverride.Trim()
            : await ResolveBranchIdAsync(config, cancellationToken).ConfigureAwait(false);

        return new TerminalActivationRequest
        {
            TerminalActivationCode = normalizedActivationKey,
            BranchCode = branch,
            Platform = new PlatformEnvironment
            {
                OsName = Environment.OSVersion.Platform.ToString(),
                OsVersion = Environment.OSVersion.Version.ToString(),
                OsBuild = Environment.OSVersion.VersionString,
                MacAddress = InstallerConfiguration.GetPrimaryMacAddress()
            },
            Pos = new PosEnvironment
            {
                ProductId = string.IsNullOrWhiteSpace(_mraOptions.ProductId)
                    ? "MRA-desktop/AlbertRetailTerminal"
                    : _mraOptions.ProductId,
                ProductVersion = string.IsNullOrWhiteSpace(_mraOptions.ProductVersion)
                    ? InstallerConfiguration.ProductVersion
                    : _mraOptions.ProductVersion
            }
        };
    }

    private async Task<string> ResolveBranchIdAsync(
        IConfigurationRepository config,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_deployment.BranchId))
        {
            return _deployment.BranchId.Trim();
        }

        var raw = await config.GetJsonAsync(DeploymentConfigurationKeys.BranchId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var trimmed = raw.Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return trimmed;
            }
        }

        return "BRANCH-001";
    }

    private async Task<MraOnboardingResult> TrySandboxFallbackOrFailAsync(
        IServiceScope scope,
        string normalizedKey,
        string upstreamMessage,
        bool confirm,
        CancellationToken cancellationToken,
        string? preferredTerminalId = null,
        int? upstreamHttpStatus = null,
        string? upstreamDiagnostic = null)
    {
        if (IsLiveProductionEnvironment())
        {
            return MraOnboardingResult.Fail(
                confirm
                    ? $"MRA confirmation failed: {upstreamMessage}"
                    : $"MRA activation failed: {upstreamMessage}",
                upstreamHttpStatus,
                upstreamDiagnostic ?? TruncateForLog(upstreamMessage));
        }

        return await CompleteSandboxLocalOnboardingAsync(
                scope,
                normalizedKey,
                cancellationToken,
                preferredTerminalId,
                upstreamHttpStatus,
                upstreamDiagnostic ?? TruncateForLog(upstreamMessage))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sandbox / offline / mock path: stages DPAPI-protected <c>TerminalCredentials</c> (JWT + secret)
    /// and Activated flag so a valid ART key (e.g. I4CV-M5YY-AKY6-Z9BT) can complete launch when live
    /// EIS is unreachable, returns HTTP 404/5xx, or rejects with a sandbox-only status.
    /// </summary>
    private async Task<MraOnboardingResult> CompleteSandboxLocalOnboardingAsync(
        IServiceScope scope,
        string normalizedKey,
        CancellationToken cancellationToken,
        string? preferredTerminalId = null,
        int? upstreamHttpStatus = null,
        string? upstreamDiagnostic = null)
    {
        var terminals = scope.ServiceProvider.GetRequiredService<ITerminalRepository>();
        var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        var terminalId = string.IsNullOrWhiteSpace(preferredTerminalId)
            ? BuildSandboxTerminalId(normalizedKey)
            : preferredTerminalId.Trim();

        var jwt = BuildSandboxJwt(terminalId);
        var secret = BuildSandboxSecret(normalizedKey);
        var branch = await ResolveBranchIdAsync(config, cancellationToken).ConfigureAwait(false);

        await terminals.UpsertPendingActivationAsync(
                new Terminal
                {
                    TerminalId = terminalId,
                    BranchCode = branch,
                    ActivationState = TerminalActivationStates.PendingConfirmation,
                    LastSyncedAt = DateTime.UtcNow
                },
                cancellationToken)
            .ConfigureAwait(false);

        await config.UpsertJsonAsync(
                MraConfigurationKeys.ActiveTerminalId,
                JsonSerializer.Serialize(new { terminalId }, MraJson.SerializerOptions),
                cancellationToken)
            .ConfigureAwait(false);

        // Persist TerminalCredentials.jwtToken (DPAPI) — mirrors live activate-terminal response.
        await config.UpsertProtectedSecretAsync(MraConfigurationKeys.JwtToken, jwt, cancellationToken)
            .ConfigureAwait(false);

        // Persist TerminalCredentials.secretKey (DPAPI) and mark Activated.
        var protectedSecret = protector.Protect(secret);
        await terminals.MarkActivatedAsync(terminalId, protectedSecret, cancellationToken)
            .ConfigureAwait(false);

        await config.UpsertJsonAsync(
                MraConfigurationKeys.PendingSecretKey,
                JsonSerializer.Serialize(new { cleared = true }, MraJson.SerializerOptions),
                cancellationToken)
            .ConfigureAwait(false);

        await config.UpsertJsonAsync(
                MraConfigurationKeys.TerminalActivationCode,
                JsonSerializer.Serialize(new { code = normalizedKey }, MraJson.SerializerOptions),
                cancellationToken)
            .ConfigureAwait(false);

        await MarkOnboardingCompleteFlagAsync(scope, cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Sandbox local MRA onboarding completed for {TerminalId}. UpstreamHttp={HttpStatus}; Diagnostic={Diagnostic}",
            terminalId,
            upstreamHttpStatus,
            TruncateForLog(upstreamDiagnostic));

        return MraOnboardingResult.Ok(
            $"Sandbox onboarding complete for terminal {terminalId}. Encrypted TerminalCredentials stored locally (live MRA EIS unavailable or returned a non-activation status).",
            terminalId,
            sandboxFallback: true,
            upstreamHttpStatus: upstreamHttpStatus,
            upstreamDiagnostic: TruncateForLog(upstreamDiagnostic));
    }

    private static async Task MarkOnboardingCompleteFlagAsync(
        IServiceScope scope,
        CancellationToken cancellationToken)
    {
        var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        await config.UpsertJsonAsync("Schema.Phase38Applied", "true", cancellationToken)
            .ConfigureAwait(false);
        await config.UpsertJsonAsync("Schema.Phase40Applied", "true", cancellationToken)
            .ConfigureAwait(false);
        await config.UpsertJsonAsync("Mra.Onboarding.Completed", "true", cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsLiveProductionEnvironment() =>
        string.Equals(_mraOptions.Environment, "Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Phase 40 — treat transport failures and <see cref="MraApiException"/> (HTTP 404/5xx from
    /// sandbox/mock gateways) as recoverable so valid ART keys can fall back securely.
    /// </summary>
    public static bool IsRecoverableMraEndpointFailure(Exception ex) =>
        ex is MraApiException
        || ex is HttpRequestException
        || ex is TaskCanceledException
        || ex is TimeoutException
        || ex.InnerException is MraApiException
        || ex.InnerException is HttpRequestException;

    private void LogRecoverableEndpointFailure(Exception ex, string operation)
    {
        if (ex is MraApiException mra)
        {
            _logger.LogWarning(
                "MRA {Operation} endpoint returned HTTP {Status}. Body={Body}",
                operation,
                mra.HttpStatusCode,
                TruncateForLog(mra.ResponseBody));
            return;
        }

        if (ex.InnerException is MraApiException innerMra)
        {
            _logger.LogWarning(
                ex,
                "MRA {Operation} failed wrapping HTTP {Status}. Body={Body}",
                operation,
                innerMra.HttpStatusCode,
                TruncateForLog(innerMra.ResponseBody));
            return;
        }

        _logger.LogWarning(ex, "MRA {Operation} unreachable or timed out.", operation);
    }

    private void LogActivationEndpointRejection(
        string? remark,
        IReadOnlyList<PointOfSale.Mra.Contracts.Common.EisApiError>? errors)
    {
        var errorSummary = errors is { Count: > 0 }
            ? string.Join("; ", errors.Select(e => $"{e.ErrorCode}:{e.FieldName}:{e.ErrorMessage}"))
            : null;

        _logger.LogWarning(
            "MRA activate-terminal business rejection. Remark={Remark}; Errors={Errors}",
            remark,
            TruncateForLog(errorSummary));
    }

    private static string BuildUpstreamMessage(Exception ex) =>
        ex is MraApiException mra
            ? $"HTTP {mra.HttpStatusCode}: {mra.Message}"
            : ex.Message;

    private static int? ExtractHttpStatus(Exception ex) =>
        ex is MraApiException mra
            ? mra.HttpStatusCode
            : ex.InnerException is MraApiException inner
                ? inner.HttpStatusCode
                : null;

    private static string? ExtractDiagnostic(Exception ex)
    {
        if (ex is MraApiException mra)
        {
            return TruncateForLog(mra.ResponseBody ?? mra.Message);
        }

        if (ex.InnerException is MraApiException inner)
        {
            return TruncateForLog(inner.ResponseBody ?? inner.Message);
        }

        return TruncateForLog(ex.Message);
    }

    private static string? TruncateForLog(string? value, int maxChars = 1500)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars] + "…";
    }

    private static string BuildSandboxTerminalId(string normalizedKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("ART.Sandbox.Terminal|" + normalizedKey));
        var suffix = Convert.ToHexString(hash.AsSpan(0, 4));
        return $"ART-SBX-{suffix}";
    }

    private static string BuildSandboxJwt(string terminalId) =>
        $"ART-SANDBOX-JWT.{terminalId}.{DateTime.UtcNow:yyyyMMdd}";

    private static string BuildSandboxSecret(string normalizedKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("ART.Sandbox.Secret|" + normalizedKey));
        return "ART-SBX-" + Convert.ToHexString(hash.AsSpan(0, 16));
    }
}
