using System.Text.Json;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.Services;

public interface IPosConfigurationService
{
    Task<PosRuntimeContext> GetRuntimeContextAsync(CancellationToken cancellationToken = default);

    string? GetConfiguredActivationCode();
}

public sealed class PosConfigurationService : IPosConfigurationService
{
    /// <summary>Sandbox / trial developer TIN used when MRA EIS Environment is not Production.</summary>
    public const string SandboxPlaceholderTaxpayerTin = "1234567890";

    private readonly IConfigurationRepository _configurationRepository;
    private readonly TerminalDeploymentOptions _deployment;
    private readonly MraApiOptions _mraOptions;

    public PosConfigurationService(
        IConfigurationRepository configurationRepository,
        IOptions<TerminalDeploymentOptions> deployment,
        IOptions<MraApiOptions> mraOptions)
    {
        _configurationRepository = configurationRepository;
        _deployment = deployment.Value;
        _mraOptions = mraOptions.Value;
    }

    public string? GetConfiguredActivationCode() =>
        IsConfiguredValue(_deployment.TerminalActivationCode)
            ? _deployment.TerminalActivationCode.Trim()
            : null;

    public async Task<PosRuntimeContext> GetRuntimeContextAsync(CancellationToken cancellationToken = default)
    {
        var globalJson = await _configurationRepository.GetJsonAsync(MraConfigurationKeys.GlobalConfiguration, cancellationToken)
            .ConfigureAwait(false);
        var terminalJson = await _configurationRepository.GetJsonAsync(MraConfigurationKeys.TerminalConfiguration, cancellationToken)
            .ConfigureAwait(false);
        var taxpayerJson = await _configurationRepository.GetJsonAsync(MraConfigurationKeys.TaxpayerConfiguration, cancellationToken)
            .ConfigureAwait(false);

        var global = Deserialize<GlobalConfigurationDto>(globalJson);
        var terminal = Deserialize<TerminalConfigurationDto>(terminalJson);
        var taxpayer = Deserialize<TaxpayerConfigurationDto>(taxpayerJson);

        var siteOverride = await ReadDeploymentStringAsync(
                DeploymentConfigurationKeys.SiteIdOverride,
                cancellationToken)
            .ConfigureAwait(false);
        var tinOverride = await ReadDeploymentStringAsync(
                DeploymentConfigurationKeys.TaxpayerTin,
                cancellationToken)
            .ConfigureAwait(false);
        var branchOverride = await ReadDeploymentStringAsync(
                DeploymentConfigurationKeys.BranchId,
                cancellationToken)
            .ConfigureAwait(false);

        string? jwtTin = null;
        try
        {
            var jwt = await _configurationRepository
                .GetProtectedSecretPlainAsync(MraConfigurationKeys.JwtToken, cancellationToken)
                .ConfigureAwait(false);
            jwtTin = PointOfSale.Mra.Security.MraJwtClaims.TryGetTaxpayerTin(jwt);
        }
        catch
        {
            // JWT may be unavailable before activation — ignore.
        }

        return new PosRuntimeContext(
            global,
            terminal,
            taxpayer,
            _deployment,
            siteOverride,
            tinOverride,
            branchOverride,
            AllowSandboxDeveloperTin: IsSandboxOrTrialEnvironment(_mraOptions.Environment),
            HostEnvironmentName: ResolveHostEnvironmentName(),
            JwtTaxpayerTin: jwtTin);
    }

    /// <summary>Sandbox / Development / trial hosts may use the developer TIN seed.</summary>
    public static bool IsSandboxOrTrialEnvironment(string? mraEnvironment) =>
        !string.Equals(mraEnvironment?.Trim(), "Production", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Operator-facing hint for which appsettings file to edit (matches ART_ENV / DOTNET_ENVIRONMENT loading).
    /// </summary>
    public static string ResolveActiveSettingsFileHint(string? hostEnvironmentName)
    {
        var env = string.IsNullOrWhiteSpace(hostEnvironmentName) ? "Sandbox" : hostEnvironmentName.Trim();
        if (env.Equals("Production", StringComparison.OrdinalIgnoreCase))
        {
            return "appsettings.Production.json";
        }

        if (env.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            return "appsettings.Development.json (or appsettings.json)";
        }

        if (env.Equals("Sandbox", StringComparison.OrdinalIgnoreCase))
        {
            return "appsettings.json (or appsettings.Sandbox.json)";
        }

        return $"appsettings.json (or appsettings.{env}.json)";
    }

    public static string BuildIncompleteConfigurationMessage(string? hostEnvironmentName)
    {
        var file = ResolveActiveSettingsFileHint(hostEnvironmentName);
        return "Run onboarding / sync MRA configs, or set BranchId, SiteId, and TaxpayerTin in "
            + $"{file} under TerminalDeployment before selling.";
    }

    private static string ResolveHostEnvironmentName() =>
        Environment.GetEnvironmentVariable("ART_ENV")
        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? "Sandbox";

    private async Task<string?> ReadDeploymentStringAsync(string key, CancellationToken cancellationToken)
    {
        var raw = await _configurationRepository.GetJsonAsync(key, cancellationToken).ConfigureAwait(false);
        return ExtractConfiguredString(raw);
    }

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? default
            : JsonSerializer.Deserialize<T>(json, MraJson.SerializerOptions);

    /// <summary>
    /// Accepts plain strings (first-run wizard) or small JSON envelopes like <c>{"tin":"..."}</c>
    /// / <c>{"siteId":"..."}</c> written by terminal provisioning.
    /// </summary>
    public static string? ExtractConfiguredString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                return NormalizeConfiguredValue(doc.RootElement.GetString());
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = NormalizeConfiguredValue(property.Value.GetString());
                        if (value is not null)
                        {
                            return value;
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            return NormalizeConfiguredValue(trimmed.Trim('"'));
        }

        return null;
    }

    public static string? NormalizeConfiguredValue(string? value)
    {
        if (!IsConfiguredValue(value))
        {
            return null;
        }

        return value!.Trim();
    }

    /// <summary>
    /// Production: skips unresolved templates and the sandbox placeholder.
    /// Sandbox/trial: accepts any configured TIN including the developer seed.
    /// </summary>
    public static string? NormalizeTaxpayerTin(string? value, bool allowSandboxDeveloperTin = false)
    {
        var normalized = NormalizeConfiguredValue(value);
        if (normalized is null)
        {
            return null;
        }

        if (!allowSandboxDeveloperTin && IsPlaceholderTaxpayerTin(normalized))
        {
            return null;
        }

        return normalized;
    }

    /// <summary>True for the historical sandbox seed TIN (blocked in Production only).</summary>
    public static bool IsPlaceholderTaxpayerTin(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Trim().Equals(SandboxPlaceholderTaxpayerTin, StringComparison.Ordinal);

    /// <summary>True when the value is present and not an unresolved template placeholder.</summary>
    public static bool IsConfiguredValue(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Contains('{', StringComparison.Ordinal)
        && !value.Contains('}', StringComparison.Ordinal);
}

public sealed record PosRuntimeContext(
    GlobalConfigurationDto? Global,
    TerminalConfigurationDto? Terminal,
    TaxpayerConfigurationDto? Taxpayer,
    TerminalDeploymentOptions? Deployment = null,
    string? DeploymentSiteId = null,
    string? DeploymentTaxpayerTin = null,
    string? DeploymentBranchId = null,
    bool AllowSandboxDeveloperTin = false,
    string? HostEnvironmentName = null,
    string? JwtTaxpayerTin = null)
{
    public string TradingName =>
        Terminal?.TradingName
        ?? Deployment?.FallbackTradingName
        ?? "Albert Retail Terminal";

    /// <summary>
    /// Seller TIN for checkout + legal receipts. Prefers a non-placeholder value from
    /// live taxpayer config, JWT claim, then deployment overrides. Sandbox may fall back
    /// to the developer seed only when no activated TIN is available.
    /// </summary>
    public string SellerTin
    {
        get
        {
            foreach (var candidate in TaxpayerTinCandidates)
            {
                var preferred = PosConfigurationService.NormalizeTaxpayerTin(candidate, allowSandboxDeveloperTin: false);
                if (!string.IsNullOrWhiteSpace(preferred))
                {
                    return preferred;
                }
            }

            if (!AllowSandboxDeveloperTin)
            {
                return string.Empty;
            }

            foreach (var candidate in TaxpayerTinCandidates)
            {
                var sandbox = PosConfigurationService.NormalizeTaxpayerTin(candidate, allowSandboxDeveloperTin: true);
                if (!string.IsNullOrWhiteSpace(sandbox))
                {
                    return sandbox;
                }
            }

            return string.Empty;
        }
    }

    private IEnumerable<string?> TaxpayerTinCandidates =>
    [
        Taxpayer?.Tin,
        JwtTaxpayerTin,
        DeploymentTaxpayerTin,
        Deployment?.TaxpayerTin
    ];

    public string SiteId =>
        PosConfigurationService.NormalizeConfiguredValue(Terminal?.TerminalSite?.SiteId)
        ?? PosConfigurationService.NormalizeConfiguredValue(DeploymentSiteId)
        ?? PosConfigurationService.NormalizeConfiguredValue(Deployment?.SiteId)
        ?? string.Empty;

    /// <summary>Site id formatted for MRA EIS submit (e.g. <c>SITE-CITY-CENTER</c>).</summary>
    public string FiscalSiteId =>
        PointOfSale.Infrastructure.Services.MraFiscalPayloadNormalizer.NormalizeSiteId(SiteId);

    public string BranchId =>
        PosConfigurationService.NormalizeConfiguredValue(DeploymentBranchId)
        ?? PosConfigurationService.NormalizeConfiguredValue(Deployment?.BranchId)
        ?? string.Empty;

    /// <summary>True when Branch, Site, and TIN are present for the active environment.</summary>
    public bool HasRequiredSalesIdentity =>
        !string.IsNullOrWhiteSpace(BranchId)
        && !string.IsNullOrWhiteSpace(SiteId)
        && !string.IsNullOrWhiteSpace(SellerTin);

    public int GlobalConfigVersion => Global?.VersionNo > 0 ? Global.VersionNo : 1;
    public int TerminalConfigVersion => Terminal?.VersionNo > 0 ? Terminal.VersionNo : 1;
    public int TaxpayerConfigVersion => Taxpayer?.VersionNo > 0 ? Taxpayer.VersionNo : 1;

    /// <summary>MRA taxRateId for the standard VAT tier — prefers configured 17.5% rate, else <c>STANDARD_17_5</c>.</summary>
    public string StandardVatTaxRateId =>
        PointOfSale.Core.Pricing.MraTaxRateCodes.ResolveStandardRateId(
            Global?.TaxRates?
                .Where(r => !string.IsNullOrWhiteSpace(r.Id) && r.Rate > 0m)
                .Select(r => (r.Id!.Trim(), r.Rate)),
            Taxpayer?.ActivatedTaxRateIds);

    public decimal ResolveVatRatePercent(string? taxRateId)
    {
        var rates = Global?.TaxRates?
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && r.Rate > 0m)
            .Select(r => (r.Id!.Trim(), r.Rate));
        return PointOfSale.Core.Pricing.MraTaxRateCodes.ResolveRatePercent(taxRateId, rates);
    }

    public IReadOnlyList<string> AddressLines => Terminal?.AddressLines ?? Array.Empty<string>();
}
