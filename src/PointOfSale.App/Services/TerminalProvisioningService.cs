using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Deployment;
using PointOfSale.App.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Models;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.Services;

public interface ITerminalProvisioningService
{
    Task<TerminalProvisioningState> GetStateAsync(CancellationToken cancellationToken = default);

    Task<LocalDeploymentPrepareResult> PrepareLocalDeploymentAsync(
        CancellationToken cancellationToken = default);

    Task<TerminalProvisioningResult> ActivateWithMraAsync(
        TerminalProvisioningRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class TerminalProvisioningRequest
{
    public required string TerminalActivationCode { get; init; }
    public string? BranchId { get; init; }
    public string? SiteId { get; init; }
    public string? TaxpayerTin { get; init; }
    public string? TerminalIdInput { get; init; }
}

public sealed class TerminalProvisioningState
{
    public bool IsProvisioned { get; init; }
    public string? ActiveTerminalId { get; init; }
    public string? TaxpayerTin { get; init; }
    public string? ActivationStatus { get; init; }
    public string HardwareFingerprintSha256 { get; init; } = string.Empty;
    public bool HardwareBindingValid { get; init; }
    public bool SqlExpressReachable { get; init; }
}

public sealed class LocalDeploymentPrepareResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> DirectoriesCreated { get; init; } = Array.Empty<string>();
}

public sealed class TerminalProvisioningResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? TerminalId { get; init; }
}

public sealed class TerminalProvisioningService : ITerminalProvisioningService
{
    private readonly TerminalOnboardingService _onboarding;
    private readonly ITerminalRepository _terminals;
    private readonly IConfigurationRepository _configuration;
    private readonly IDatabaseBootstrapService _databaseBootstrap;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly TerminalDeploymentOptions _deployment;
    private readonly InstallerPackagingOptions _packaging;
    private readonly MraApiOptions _mraOptions;
    private readonly DatabaseBootstrapOptions _bootstrapOptions;
    private readonly ILogger<TerminalProvisioningService> _logger;

    public TerminalProvisioningService(
        TerminalOnboardingService onboarding,
        ITerminalRepository terminals,
        IConfigurationRepository configuration,
        IDatabaseBootstrapService databaseBootstrap,
        IAuthenticationAuthorizationService auth,
        IOptions<TerminalDeploymentOptions> deployment,
        IOptions<InstallerPackagingOptions> packaging,
        IOptions<MraApiOptions> mraOptions,
        IOptions<DatabaseBootstrapOptions> bootstrapOptions,
        ILogger<TerminalProvisioningService> logger)
    {
        _onboarding = onboarding;
        _terminals = terminals;
        _configuration = configuration;
        _databaseBootstrap = databaseBootstrap;
        _auth = auth;
        _deployment = deployment.Value;
        _packaging = packaging.Value;
        _mraOptions = mraOptions.Value;
        _bootstrapOptions = bootstrapOptions.Value;
        _logger = logger;
    }

    public async Task<TerminalProvisioningState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var fingerprint = InstallerConfiguration.ComputeHardwareFingerprintSha256();
        var storedFingerprint = await ReadConfigJsonStringAsync(
                DeploymentConfigurationKeys.HardwareFingerprintSha256,
                cancellationToken)
            .ConfigureAwait(false);

        var hardwareValid = !_packaging.EnforceHardwareBinding
            || string.IsNullOrWhiteSpace(storedFingerprint)
            || InstallerConfiguration.HardwareFingerprintsMatch(storedFingerprint);

        var terminalId = await _terminals.GetActiveTerminalIdAsync(cancellationToken).ConfigureAwait(false);
        var tin = await ReadConfigJsonStringAsync(DeploymentConfigurationKeys.TaxpayerTin, cancellationToken)
            .ConfigureAwait(false);

        var sqlOk = await ProbeSqlExpressAsync(cancellationToken).ConfigureAwait(false);

        var isProvisioned = !string.IsNullOrWhiteSpace(terminalId) && hardwareValid;

        return new TerminalProvisioningState
        {
            IsProvisioned = isProvisioned,
            ActiveTerminalId = terminalId,
            TaxpayerTin = tin,
            HardwareFingerprintSha256 = fingerprint,
            HardwareBindingValid = hardwareValid,
            SqlExpressReachable = sqlOk,
            ActivationStatus = isProvisioned
                ? $"Terminal {terminalId} is activated and bound to this device."
                : hardwareValid
                    ? "Not activated — enter the MRA EIS Portal activation key to register this till."
                    : "Hardware binding mismatch — contact support before activating."
        };
    }

    public async Task<LocalDeploymentPrepareResult> PrepareLocalDeploymentAsync(
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.ProvisionTerminal);

        var created = new List<string>();
        foreach (var path in InstallerConfiguration.ResolveStandardDirectoryPaths(_packaging))
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
                created.Add(path);
            }
        }

        try
        {
            await _databaseBootstrap.EnsureDatabaseReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database bootstrap failed during deployment preparation.");
            return new LocalDeploymentPrepareResult
            {
                Success = false,
                Message = ex.Message,
                DirectoriesCreated = created
            };
        }

        var fingerprint = InstallerConfiguration.ComputeHardwareFingerprintSha256();
        var existing = await ReadConfigJsonStringAsync(
                DeploymentConfigurationKeys.HardwareFingerprintSha256,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(existing))
        {
            await _configuration.UpsertJsonAsync(
                    DeploymentConfigurationKeys.HardwareFingerprintSha256,
                    JsonSerializer.Serialize(new { sha256 = fingerprint }, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (_packaging.EnforceHardwareBinding
                 && !InstallerConfiguration.HardwareFingerprintsMatch(existing))
        {
            return new LocalDeploymentPrepareResult
            {
                Success = false,
                Message = "This installation is bound to a different hardware fingerprint.",
                DirectoriesCreated = created
            };
        }

        var sqlOk = await ProbeSqlExpressAsync(cancellationToken).ConfigureAwait(false);
        var sqlMessage = sqlOk
            ? $"SQL Express ({_bootstrapOptions.RequiredInstanceHint}) is reachable."
            : $"SQL Express instance '{_packaging.SqlExpressInstanceName}' was not detected. "
              + "Install SQL Server Express or run the silent setup from Deployment documentation.";

        return new LocalDeploymentPrepareResult
        {
            Success = sqlOk,
            Message = $"Local deployment folders ready. {sqlMessage}",
            DirectoriesCreated = created
        };
    }

    public async Task<TerminalProvisioningResult> ActivateWithMraAsync(
        TerminalProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        _auth.EnsurePermission(OperatorPermissions.ProvisionTerminal);

        if (string.IsNullOrWhiteSpace(request.TerminalActivationCode))
        {
            throw new InvalidOperationException("Terminal activation code (TAC) is required.");
        }

        var prepare = await PrepareLocalDeploymentAsync(cancellationToken).ConfigureAwait(false);
        if (!prepare.Success)
        {
            return new TerminalProvisioningResult { Success = false, Message = prepare.Message };
        }

        var branch = (request.BranchId ?? _deployment.BranchId).Trim();
        if (string.IsNullOrWhiteSpace(branch))
        {
            throw new InvalidOperationException("Branch ID is required for terminal provisioning.");
        }

        var platform = new PlatformEnvironment
        {
            OsName = Environment.OSVersion.Platform.ToString(),
            OsVersion = Environment.OSVersion.Version.ToString(),
            OsBuild = Environment.OSVersion.VersionString,
            MacAddress = InstallerConfiguration.GetPrimaryMacAddress()
        };

        var activationRequest = new TerminalActivationRequest
        {
            TerminalActivationCode = request.TerminalActivationCode.Trim(),
            BranchCode = branch,
            Platform = platform,
            Pos = new PosEnvironment
            {
                ProductId = _mraOptions.ProductId,
                ProductVersion = _mraOptions.ProductVersion
            }
        };

        var activate = await _onboarding.ActivateTerminalAsync(activationRequest, cancellationToken)
            .ConfigureAwait(false);
        if (!activate.Success || string.IsNullOrWhiteSpace(activate.TerminalId))
        {
            var remark = activate.Remark ?? "MRA activation failed.";
            return new TerminalProvisioningResult { Success = false, Message = remark };
        }

        if (!string.IsNullOrWhiteSpace(request.TerminalIdInput)
            && !string.Equals(request.TerminalIdInput.Trim(), activate.TerminalId, StringComparison.OrdinalIgnoreCase))
        {
            return new TerminalProvisioningResult
            {
                Success = false,
                Message = "Supplied terminal ID does not match MRA activation response."
            };
        }

        var confirm = await _onboarding.ConfirmTerminalActivationAsync(
                new TerminalActivationConfirmationRequest
                {
                    TerminalId = activate.TerminalId,
                    TerminalActivationCode = request.TerminalActivationCode.Trim()
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (!confirm.Success)
        {
            return new TerminalProvisioningResult
            {
                Success = false,
                TerminalId = activate.TerminalId,
                Message = confirm.Remark ?? "MRA confirmation failed. Retry confirmation before selling."
            };
        }

        if (!string.IsNullOrWhiteSpace(request.TaxpayerTin))
        {
            await _configuration.UpsertJsonAsync(
                    DeploymentConfigurationKeys.TaxpayerTin,
                    JsonSerializer.Serialize(new { tin = request.TaxpayerTin.Trim() }, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(request.SiteId ?? _deployment.SiteId))
        {
            var site = (request.SiteId ?? _deployment.SiteId).Trim();
            await _configuration.UpsertJsonAsync(
                    DeploymentConfigurationKeys.SiteIdOverride,
                    JsonSerializer.Serialize(new { siteId = site }, MraJson.SerializerOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await _configuration.UpsertJsonAsync(
                DeploymentConfigurationKeys.ProvisionedAtUtc,
                JsonSerializer.Serialize(new { utc = DateTime.UtcNow }, MraJson.SerializerOptions),
                cancellationToken)
            .ConfigureAwait(false);

        var configSync = await _onboarding.GetLatestConfigsAsync(cancellationToken).ConfigureAwait(false);
        if (configSync.UsedLocalFallback)
        {
            _logger.LogWarning(
                "Terminal {TerminalId} activated; live configuration sync unavailable — using local activation fallback. {Remark}",
                activate.TerminalId,
                configSync.Remark);
        }
        else if (!configSync.Success)
        {
            _logger.LogWarning(
                "Terminal {TerminalId} activated but configuration sync failed: {Remark}",
                activate.TerminalId,
                configSync.Remark);
        }

        _logger.LogInformation(
            "Terminal {TerminalId} provisioned with MRA EIS (DPAPI secrets stored).",
            activate.TerminalId);

        return new TerminalProvisioningResult
        {
            Success = true,
            TerminalId = activate.TerminalId,
            Message = confirm.Remark
                ?? $"Terminal {activate.TerminalId} activated. Cryptographic credentials sealed with DPAPI."
        };
    }

    private async Task<bool> ProbeSqlExpressAsync(CancellationToken cancellationToken)
    {
        if (!_packaging.AttemptSqlExpressDetection)
        {
            return true;
        }

        try
        {
            await _databaseBootstrap.EnsureDatabaseReadyAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> ReadConfigJsonStringAsync(string key, CancellationToken cancellationToken)
    {
        var json = await _configuration.GetJsonAsync(key, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            return json.Trim('"');
        }

        return null;
    }
}
