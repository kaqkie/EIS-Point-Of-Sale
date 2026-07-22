using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using PointOfSale.App.Deployment;
using PointOfSale.App.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface IFirstRunBootstrapService
{
    bool IsFirstRunComplete { get; }

    event EventHandler? FirstRunStatusChanged;

    Task<FirstRunBootstrapResult> EnsureInfrastructureAsync(CancellationToken cancellationToken = default);

    Task<FirstRunBootstrapResult> CompleteSetupAsync(
        FirstRunSetupRequest request,
        CancellationToken cancellationToken = default);

    Task RefreshStatusAsync(CancellationToken cancellationToken = default);
}

public sealed class FirstRunSetupRequest
{
    public required string TerminalDisplayName { get; init; }
    public required string BranchId { get; init; }
    public string? SiteId { get; init; }
    public string MraEnvironment { get; init; } = "Sandbox";
    public string? LicenseKey { get; init; }
}

public sealed class FirstRunBootstrapResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public SqlEngineKind DetectedEngine { get; init; }
    public bool SchemaReady { get; init; }
    public bool OperatorsSeeded { get; init; }
    public bool VatConfigured { get; init; }
    public bool FirstRunComplete { get; init; }

    public static FirstRunBootstrapResult Ok(string message, SqlEngineKind engine, bool complete = false) =>
        new()
        {
            Success = true,
            Message = message,
            DetectedEngine = engine,
            SchemaReady = true,
            OperatorsSeeded = true,
            VatConfigured = true,
            FirstRunComplete = complete
        };

    public static FirstRunBootstrapResult Fail(string message, SqlEngineKind engine = SqlEngineKind.None) =>
        new() { Success = false, Message = message, DetectedEngine = engine };
}

/// <summary>
/// Phase 35 first-run orchestrator: SQL Express/LocalDB detection, schema bootstrap,
/// admin/cashier seed, statutory 17.5% VAT, and first-run completion flag.
/// </summary>
public sealed class FirstRunBootstrapService : IFirstRunBootstrapService
{
    public const string RegistryKeyPath = @"Software\AlbertRetail\AlbertRetailTerminal";
    public const string RegistryFirstRunValue = "FirstRunCompleted";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDatabaseBootstrapService _databaseBootstrap;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly ITerminalActivationService _activation;
    private readonly InstallerPackagingOptions _packaging;
    private readonly ILogger<FirstRunBootstrapService> _logger;

    private bool _firstRunComplete;

    public FirstRunBootstrapService(
        IServiceScopeFactory scopeFactory,
        IDatabaseBootstrapService databaseBootstrap,
        IAuthenticationAuthorizationService auth,
        ITerminalActivationService activation,
        IOptions<InstallerPackagingOptions> packaging,
        ILogger<FirstRunBootstrapService> logger)
    {
        _scopeFactory = scopeFactory;
        _databaseBootstrap = databaseBootstrap;
        _auth = auth;
        _activation = activation;
        _packaging = packaging.Value;
        _logger = logger;
        _firstRunComplete = ReadRegistryFirstRunComplete();
    }

    public event EventHandler? FirstRunStatusChanged;

    public bool IsFirstRunComplete => _firstRunComplete;

    public async Task RefreshStatusAsync(CancellationToken cancellationToken = default)
    {
        var fromRegistry = ReadRegistryFirstRunComplete();
        var fromConfig = false;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
            var flag = await config.GetJsonAsync(DeploymentConfigurationKeys.FirstRunCompleted, cancellationToken)
                .ConfigureAwait(false);
            fromConfig = string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // DB may not be ready yet.
        }

        _firstRunComplete = fromRegistry || fromConfig;
        FirstRunStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<FirstRunBootstrapResult> EnsureInfrastructureAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            InstallerConfiguration.EnsureStandardDirectories(_packaging);

            var engine = SqlEngineKind.None;
            if (_packaging.AttemptSqlExpressDetection)
            {
                engine = InstallerConfiguration.DetectSqlEngine(
                    _packaging.SqlExpressInstanceName,
                    _packaging.LocalDbInstanceName,
                    _packaging.AllowLocalDbFallback);

                if (engine == SqlEngineKind.None)
                {
                    return FirstRunBootstrapResult.Fail(
                        "Cannot reach SQL Server Express or LocalDB. " +
                        "Install SQLEXPRESS / LocalDB and optionally run Setup\\Bootstrap-SqlExpressOrLocalDb.ps1.");
                }

                if (engine == SqlEngineKind.LocalDb)
                {
                    var cs = InstallerConfiguration.BuildLocalDbConnectionString(
                        instance: _packaging.LocalDbInstanceName);
                    InstallerConfiguration.WriteDeploymentConnectionOverride(
                        cs,
                        $@"(localdb)\{_packaging.LocalDbInstanceName}");
                    _logger.LogWarning(
                        "SQL Express unavailable — wrote LocalDB deployment override to {Path}.",
                        InstallerConfiguration.ResolveDeploymentOverridePath());
                }
            }

            await _databaseBootstrap.EnsureDatabaseReadyAsync(cancellationToken).ConfigureAwait(false);
            await _auth.EnsureSeededAsync(cancellationToken).ConfigureAwait(false);
            await EnsureStatutoryVatAsync(cancellationToken).ConfigureAwait(false);
            await RefreshStatusAsync(cancellationToken).ConfigureAwait(false);

            return new FirstRunBootstrapResult
            {
                Success = true,
                Message = _firstRunComplete
                    ? "Infrastructure ready (first-run already completed)."
                    : "Infrastructure ready — complete the first-run setup wizard.",
                DetectedEngine = engine,
                SchemaReady = true,
                OperatorsSeeded = true,
                VatConfigured = true,
                FirstRunComplete = _firstRunComplete
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "First-run infrastructure bootstrap failed.");
            return FirstRunBootstrapResult.Fail(ex.Message);
        }
    }

    public async Task<FirstRunBootstrapResult> CompleteSetupAsync(
        FirstRunSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TerminalDisplayName))
        {
            return FirstRunBootstrapResult.Fail("Terminal display name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.BranchId))
        {
            return FirstRunBootstrapResult.Fail("Branch identifier is required.");
        }

        var environment = string.Equals(request.MraEnvironment, "Production", StringComparison.OrdinalIgnoreCase)
            ? "Production"
            : "Sandbox";

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();

            await config.UpsertJsonAsync(
                    DeploymentConfigurationKeys.TerminalDisplayName,
                    request.TerminalDisplayName.Trim(),
                    cancellationToken)
                .ConfigureAwait(false);
            await config.UpsertJsonAsync(
                    DeploymentConfigurationKeys.BranchId,
                    request.BranchId.Trim(),
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(request.SiteId))
            {
                await config.UpsertJsonAsync(
                        DeploymentConfigurationKeys.SiteIdOverride,
                        request.SiteId.Trim(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await config.UpsertJsonAsync(
                    DeploymentConfigurationKeys.MraEnvironmentPreference,
                    environment,
                    cancellationToken)
                .ConfigureAwait(false);

            // Persist ART_ENV hint for next process start (sandbox vs production appsettings).
            Environment.SetEnvironmentVariable("ART_ENV", environment, EnvironmentVariableTarget.User);

            if (!string.IsNullOrWhiteSpace(request.LicenseKey))
            {
                var activation = await _activation.ActivateAsync(request.LicenseKey.Trim(), cancellationToken)
                    .ConfigureAwait(false);
                if (!activation.Success)
                {
                    return FirstRunBootstrapResult.Fail(activation.Message);
                }
            }

            await EnsureStatutoryVatAsync(cancellationToken).ConfigureAwait(false);

            await config.UpsertJsonAsync(
                    DeploymentConfigurationKeys.FirstRunCompleted,
                    "true",
                    cancellationToken)
                .ConfigureAwait(false);

            WriteRegistryFirstRunComplete();
            _firstRunComplete = true;
            FirstRunStatusChanged?.Invoke(this, EventArgs.Empty);

            return FirstRunBootstrapResult.Ok(
                $"First-run setup complete for '{request.TerminalDisplayName}' ({environment}).",
                InstallerConfiguration.DetectSqlEngine(
                    _packaging.SqlExpressInstanceName,
                    _packaging.LocalDbInstanceName,
                    _packaging.AllowLocalDbFallback),
                complete: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "First-run setup completion failed.");
            return FirstRunBootstrapResult.Fail(ex.Message);
        }
    }

    private async Task EnsureStatutoryVatAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfigurationRepository>();
        await config.UpsertJsonAsync(
                "Fiscal.StandardVatRatePercent",
                PosTaxCalculator.MalawiStandardVatRatePercent.ToString("0.0"),
                cancellationToken)
            .ConfigureAwait(false);
        await config.UpsertJsonAsync(
                "Fiscal.VatRuleSource",
                "PosTaxCalculator.MalawiStandardVatRatePercent",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool ReadRegistryFirstRunComplete()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            var value = key?.GetValue(RegistryFirstRunValue);
            return value is int i && i == 1
                   || value is string s && string.Equals(s, "1", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void WriteRegistryFirstRunComplete()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryKeyPath);
        key?.SetValue(RegistryFirstRunValue, 1, RegistryValueKind.DWord);
    }
}
