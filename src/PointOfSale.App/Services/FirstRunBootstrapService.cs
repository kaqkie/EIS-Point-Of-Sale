using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
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
    public string? TaxpayerTin { get; init; }
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
/// Phase 35 first-run orchestrator (+ Phase 37 reserved-keyword SQL Express hardening):
/// SQL Express/LocalDB detection, schema bootstrap via bracket-safe T-SQL,
/// admin/cashier seed, statutory 17.5% VAT, and first-run completion flag.
/// </summary>
public sealed class FirstRunBootstrapService : IFirstRunBootstrapService
{
    public const string RegistryKeyPath = @"Software\AlbertRetail\AlbertRetailTerminal";
    public const string RegistryFirstRunValue = "FirstRunCompleted";

    /// <summary>Canonical Phase 37 sqlcmd companion (GO-batched, [Trigger] delimited).</summary>
    public const string InitialSetupScriptRelativePath = @"Database\Scripts\InitialSetup.sql";

    private static readonly Regex GoBatchSplitter = new(
        @"^\s*GO\s*(?:--.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IDatabaseBootstrapService _databaseBootstrap;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly ITerminalActivationService _activation;
    private readonly IConfiguration _configuration;
    private readonly InstallerPackagingOptions _packaging;
    private readonly ILogger<FirstRunBootstrapService> _logger;

    private bool _firstRunComplete;

    public FirstRunBootstrapService(
        IServiceScopeFactory scopeFactory,
        IDatabaseBootstrapService databaseBootstrap,
        IAuthenticationAuthorizationService auth,
        ITerminalActivationService activation,
        IConfiguration configuration,
        IOptions<InstallerPackagingOptions> packaging,
        ILogger<FirstRunBootstrapService> logger)
    {
        _scopeFactory = scopeFactory;
        _databaseBootstrap = databaseBootstrap;
        _auth = auth;
        _activation = activation;
        _configuration = configuration;
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

            // Phase 37: optional sqlcmd-parity InitialSetup.sql (GO batches + [Trigger] delimiters).
            // Safe no-op when the script is absent; never replaces the in-app schema bootstrapper.
            await TryApplyInitialSetupScriptAsync(cancellationToken).ConfigureAwait(false);

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

            if (!string.IsNullOrWhiteSpace(request.TaxpayerTin))
            {
                await config.UpsertJsonAsync(
                        DeploymentConfigurationKeys.TaxpayerTin,
                        JsonSerializer.Serialize(new { tin = request.TaxpayerTin.Trim() }),
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

            var terminalIdJson = await config.GetJsonAsync(MraConfigurationKeys.ActiveTerminalId, cancellationToken)
                .ConfigureAwait(false);
            var terminalId = PosConfigurationService.ExtractConfiguredString(terminalIdJson)
                ?? InstallerConfiguration.ComputeHardwareFingerprintSha256()[..8];

            var deployment = _configuration
                .GetSection(TerminalDeploymentOptions.SectionName)
                .Get<TerminalDeploymentOptions>() ?? new TerminalDeploymentOptions();

            await LocalFiscalIdentitySeeder.SeedAsync(
                    config,
                    terminalId,
                    request.BranchId.Trim(),
                    request.SiteId,
                    request.TaxpayerTin,
                    request.TerminalDisplayName.Trim(),
                    cancellationToken,
                    addressLines: deployment.MerchantAddressLines,
                    contactPhone: deployment.ContactPhone,
                    contactEmail: deployment.ContactEmail)
                .ConfigureAwait(false);

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

    /// <summary>
    /// Resolves the Phase 37 InitialSetup.sql packaged next to the executable (or under the repo during development).
    /// </summary>
    public static string? ResolveInitialSetupScriptPath()
    {
        foreach (var candidate in EnumerateInitialSetupCandidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Splits a sqlcmd-style script on standalone GO batches (ignored by SqlClient as a single command).
    /// </summary>
    public static IReadOnlyList<string> SplitSqlGoBatches(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return Array.Empty<string>();
        }

        return GoBatchSplitter
            .Split(script)
            .Select(batch => batch.Trim())
            .Where(batch => batch.Length > 0)
            .ToArray();
    }

    private async Task TryApplyInitialSetupScriptAsync(CancellationToken cancellationToken)
    {
        var scriptPath = ResolveInitialSetupScriptPath();
        if (scriptPath is null)
        {
            _logger.LogDebug(
                "Phase 37 InitialSetup.sql not found beside the app; relying on in-app DatabaseBootstrapService.");
            return;
        }

        var connectionString = _configuration.GetConnectionString("PosDatabase");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning("PosDatabase connection string missing; skipped InitialSetup.sql apply.");
            return;
        }

        string script;
        try
        {
            script = await File.ReadAllTextAsync(scriptPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to read Phase 37 InitialSetup script at {Path}.", scriptPath);
            return;
        }

        // Skip CREATE DATABASE / USE batches — DatabaseBootstrapService already owns catalog lifecycle.
        var batches = SplitSqlGoBatches(script)
            .Where(batch =>
                !batch.Contains("CREATE DATABASE", StringComparison.OrdinalIgnoreCase)
                && !Regex.IsMatch(batch, @"^\s*USE\s+", RegexOptions.IgnoreCase | RegexOptions.Multiline))
            .ToArray();

        if (batches.Length == 0)
        {
            return;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            foreach (var batch in batches)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = batch;
                command.CommandTimeout = 120;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Phase 37 InitialSetup.sql applied ({BatchCount} GO batches) from {Path}.",
                batches.Length,
                scriptPath);
        }
        catch (Exception ex)
        {
            // Non-fatal: in-app bootstrap already provisioned schema; script is idempotent companion.
            _logger.LogWarning(
                ex,
                "Phase 37 InitialSetup.sql apply skipped after error (schema already bootstrapped in-app).");
        }
    }

    private static IEnumerable<string> EnumerateInitialSetupCandidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "Database", "Scripts", "InitialSetup.sql");
        yield return Path.Combine(AppContext.BaseDirectory, "Scripts", "InitialSetup.sql");

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "Database", "Scripts", "InitialSetup.sql");
            yield return Path.Combine(dir.FullName, "database", "Scripts", "InitialSetup.sql");
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
