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

        return new PosRuntimeContext(
            global,
            terminal,
            taxpayer,
            _deployment,
            siteOverride,
            tinOverride,
            branchOverride,
            AllowSandboxDeveloperTin: IsSandboxOrTrialEnvironment(_mraOptions.Environment),
            HostEnvironmentName: ResolveHostEnvironmentName());
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
    string? HostEnvironmentName = null)
{
    public string TradingName =>
        Terminal?.TradingName
        ?? Deployment?.FallbackTradingName
        ?? "Albert Retail Terminal";

    /// <summary>
    /// Seller TIN for checkout + legal receipts — prefers live MRA taxpayer config, then SQL
    /// deployment override, then <see cref="TerminalDeploymentOptions.TaxpayerTin"/>.
    /// In Sandbox/trial, the developer TIN seed is accepted; Production still rejects it.
    /// </summary>
    public string SellerTin =>
        PosConfigurationService.NormalizeTaxpayerTin(Taxpayer?.Tin, AllowSandboxDeveloperTin)
        ?? PosConfigurationService.NormalizeTaxpayerTin(DeploymentTaxpayerTin, AllowSandboxDeveloperTin)
        ?? PosConfigurationService.NormalizeTaxpayerTin(Deployment?.TaxpayerTin, AllowSandboxDeveloperTin)
        ?? string.Empty;

    public string SiteId =>
        PosConfigurationService.NormalizeConfiguredValue(Terminal?.TerminalSite?.SiteId)
        ?? PosConfigurationService.NormalizeConfiguredValue(DeploymentSiteId)
        ?? PosConfigurationService.NormalizeConfiguredValue(Deployment?.SiteId)
        ?? string.Empty;

    public string BranchId =>
        PosConfigurationService.NormalizeConfiguredValue(DeploymentBranchId)
        ?? PosConfigurationService.NormalizeConfiguredValue(Deployment?.BranchId)
        ?? string.Empty;

    /// <summary>True when Branch, Site, and TIN are present for the active environment.</summary>
    public bool HasRequiredSalesIdentity =>
        !string.IsNullOrWhiteSpace(BranchId)
        && !string.IsNullOrWhiteSpace(SiteId)
        && !string.IsNullOrWhiteSpace(SellerTin);

    public int GlobalConfigVersion => Global?.VersionNo ?? 0;
    public int TerminalConfigVersion => Terminal?.VersionNo ?? 0;
    public int TaxpayerConfigVersion => Taxpayer?.VersionNo ?? 0;
    public IReadOnlyList<string> AddressLines => Terminal?.AddressLines ?? Array.Empty<string>();
}
