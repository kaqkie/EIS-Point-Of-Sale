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

    public static MraOnboardingResult Ok(
        string message,
        string? terminalId = null,
        bool sandboxFallback = false) =>
        new()
        {
            Success = true,
            Message = message,
            TerminalId = terminalId,
            UsedSandboxLocalFallback = sandboxFallback
        };

    public static MraOnboardingResult Fail(string message) =>
        new() { Success = false, Message = message };
}

/// <summary>
/// Phase 38 — App-layer facade over official MRA EIS onboarding endpoints
/// (<c>onboarding/activate-terminal</c>, <c>onboarding/terminal-activated-confirmation</c>).
/// Persists JWT / secret keys via DPAPI into SQL Express and marks the terminal Activated.
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
                return await TrySandboxFallbackOrFailAsync(
                        scope,
                        normalized,
                        result.Remark ?? "MRA activate-terminal rejected the activation key.",
                        confirm: false,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return MraOnboardingResult.Ok(
                result.Remark ?? $"Terminal {result.TerminalId} pending MRA confirmation.",
                result.TerminalId);
        }
        catch (Exception ex) when (IsTransientMraFailure(ex))
        {
            _logger.LogWarning(ex, "MRA activate-terminal unreachable; evaluating sandbox fallback.");
            using var scope = _scopeFactory.CreateScope();
            return await TrySandboxFallbackOrFailAsync(
                    scope,
                    normalized,
                    ex.Message,
                    confirm: false,
                    cancellationToken)
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
                return MraOnboardingResult.Fail(
                    result.Remark ?? "MRA terminal-activated-confirmation failed.");
            }

            await MarkOnboardingCompleteFlagAsync(scope, cancellationToken).ConfigureAwait(false);

            return MraOnboardingResult.Ok(
                result.Remark ?? $"Terminal {terminalId} confirmed with MRA.",
                terminalId.Trim());
        }
        catch (Exception ex) when (IsTransientMraFailure(ex))
        {
            _logger.LogWarning(ex, "MRA confirmation unreachable for {TerminalId}.", terminalId);
            using var scope = _scopeFactory.CreateScope();
            return await TrySandboxFallbackOrFailAsync(
                    scope,
                    normalized,
                    ex.Message,
                    confirm: true,
                    cancellationToken,
                    preferredTerminalId: terminalId.Trim())
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
            catch (Exception ex) when (IsTransientMraFailure(ex))
            {
                _logger.LogWarning(ex, "MRA activate-terminal unreachable during ActivateAndConfirm.");
                return await CompleteSandboxLocalOnboardingAsync(scope, normalized, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!activate.Success || string.IsNullOrWhiteSpace(activate.TerminalId))
            {
                // Invalid TAC from live MRA — do not silently unlock production.
                if (IsLiveProductionEnvironment())
                {
                    return MraOnboardingResult.Fail(
                        activate.Remark ?? "MRA rejected the activation key.");
                }

                _logger.LogWarning(
                    "MRA activate-terminal returned failure ({Remark}); applying sandbox local onboarding for valid ART key.",
                    activate.Remark);
                return await CompleteSandboxLocalOnboardingAsync(scope, normalized, cancellationToken)
                    .ConfigureAwait(false);
            }

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
            catch (Exception ex) when (IsTransientMraFailure(ex))
            {
                _logger.LogWarning(ex, "MRA confirmation unreachable after activate; completing sandbox local path.");
                return await CompleteSandboxLocalOnboardingAsync(
                        scope,
                        normalized,
                        cancellationToken,
                        preferredTerminalId: activate.TerminalId)
                    .ConfigureAwait(false);
            }

            if (!confirm.Success)
            {
                if (IsLiveProductionEnvironment())
                {
                    return MraOnboardingResult.Fail(
                        confirm.Remark ?? "MRA confirmation failed after activate-terminal.");
                }

                return await CompleteSandboxLocalOnboardingAsync(
                        scope,
                        normalized,
                        cancellationToken,
                        preferredTerminalId: activate.TerminalId)
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
            return MraOnboardingResult.Fail(ex.Message);
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
        string? preferredTerminalId = null)
    {
        if (IsLiveProductionEnvironment())
        {
            return MraOnboardingResult.Fail(
                confirm
                    ? $"MRA confirmation failed: {upstreamMessage}"
                    : $"MRA activation failed: {upstreamMessage}");
        }

        return await CompleteSandboxLocalOnboardingAsync(
                scope,
                normalizedKey,
                cancellationToken,
                preferredTerminalId)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sandbox / offline path: stages DPAPI-protected credentials and Activated flag so a valid
    /// ART key (e.g. I4CV-M5YY-AKY6-Z9BT) can complete launch when live EIS is unreachable.
    /// </summary>
    private async Task<MraOnboardingResult> CompleteSandboxLocalOnboardingAsync(
        IServiceScope scope,
        string normalizedKey,
        CancellationToken cancellationToken,
        string? preferredTerminalId = null)
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

        await config.UpsertProtectedSecretAsync(MraConfigurationKeys.JwtToken, jwt, cancellationToken)
            .ConfigureAwait(false);

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
            "Sandbox local MRA onboarding completed for {TerminalId} (live EIS unavailable or rejected).",
            terminalId);

        return MraOnboardingResult.Ok(
            $"Sandbox onboarding complete for terminal {terminalId}. Encrypted credentials stored locally (live MRA EIS unavailable).",
            terminalId,
            sandboxFallback: true);
    }

    private static async Task MarkOnboardingCompleteFlagAsync(
        IServiceScope scope,
        CancellationToken cancellationToken)
    {
        var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        await config.UpsertJsonAsync("Schema.Phase38Applied", "true", cancellationToken)
            .ConfigureAwait(false);
        await config.UpsertJsonAsync("Mra.Onboarding.Completed", "true", cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsLiveProductionEnvironment() =>
        string.Equals(_mraOptions.Environment, "Production", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransientMraFailure(Exception ex) =>
        ex is HttpRequestException
        || ex is TaskCanceledException
        || ex is TimeoutException
        || ex.InnerException is HttpRequestException;

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
