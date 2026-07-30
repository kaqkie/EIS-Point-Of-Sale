using System.Text.Json;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Billing;
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
        var merchantAddressOverride = await ReadDeploymentAddressLinesAsync(cancellationToken)
            .ConfigureAwait(false);
        var merchantPhoneOverride = await ReadDeploymentStringAsync(
                DeploymentConfigurationKeys.MerchantPhone,
                cancellationToken)
            .ConfigureAwait(false);
        var merchantEmailOverride = await ReadDeploymentStringAsync(
                DeploymentConfigurationKeys.MerchantEmail,
                cancellationToken)
            .ConfigureAwait(false);
        var terminalPosition = await ReadTerminalPositionAsync(cancellationToken).ConfigureAwait(false);
        var fiscalTaxpayerId = await ReadFiscalTaxpayerIdAsync(cancellationToken).ConfigureAwait(false);

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
            JwtTaxpayerTin: jwtTin,
            TerminalPosition: terminalPosition,
            FiscalTaxpayerId: fiscalTaxpayerId,
            DeploymentMerchantAddressLines: merchantAddressOverride,
            DeploymentContactPhone: merchantPhoneOverride,
            DeploymentContactEmail: merchantEmailOverride);
    }

    private async Task<IReadOnlyList<string>?> ReadDeploymentAddressLinesAsync(CancellationToken cancellationToken)
    {
        var raw = await _configurationRepository
            .GetJsonAsync(DeploymentConfigurationKeys.MerchantAddress, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var lines = doc.RootElement.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => NormalizeConfiguredValue(e.GetString()))
                    .Where(v => v is not null)
                    .Select(v => v!)
                    .ToList();
                return lines.Count == 0 ? null : lines;
            }
        }
        catch (JsonException)
        {
            // Fall through to single-string extraction.
        }

        var single = ExtractConfiguredString(raw);
        return single is null ? null : [single];
    }

    private async Task<int> ReadTerminalPositionAsync(CancellationToken cancellationToken)
    {
        var json = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.TerminalPosition, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return 1;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("position", out var positionElement) &&
                positionElement.TryGetInt32(out var position) &&
                position > 0)
            {
                return position;
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return 1;
    }

    private async Task<long?> ReadFiscalTaxpayerIdAsync(CancellationToken cancellationToken)
    {
        var json = await _configurationRepository
            .GetJsonAsync(MraConfigurationKeys.TaxpayerId, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("taxpayerId", out var idElement)
                && idElement.TryGetInt64(out var id)
                && id > 0)
            {
                return id;
            }
        }
        catch (JsonException)
        {
            // ignore
        }

        return null;
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
    string? JwtTaxpayerTin = null,
    int TerminalPosition = 1,
    long? FiscalTaxpayerId = null,
    IReadOnlyList<string>? DeploymentMerchantAddressLines = null,
    string? DeploymentContactPhone = null,
    string? DeploymentContactEmail = null)
{
    public string TradingName =>
        Terminal?.TradingName
        ?? Deployment?.FallbackTradingName
        ?? "Albert Retail Terminal";

    /// <summary>
    /// Numeric id encoded into MRA composite invoice numbers (Base64 first segment).
    /// Prefers activation <c>taxpayerId</c> when it differs from seller TIN digits.
    /// </summary>
    public long ResolveFiscalTaxpayerId()
    {
        if (FiscalTaxpayerId is > 0)
        {
            return FiscalTaxpayerId.Value;
        }

        return MraInvoiceNumberGenerator.TryParseTaxpayerId(SellerTin, out var fromTin)
            ? fromTin
            : 0;
    }

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

    public int GlobalConfigVersion => Global?.VersionNo > 0 ? (int)Global.VersionNo : 1;
    public int TerminalConfigVersion => Terminal?.VersionNo > 0 ? (int)Terminal.VersionNo : 1;
    public int TaxpayerConfigVersion => Taxpayer?.VersionNo > 0 ? (int)Taxpayer.VersionNo : 1;

    /// <summary>MRA taxRateId for the standard VAT tier — prefers activated config ids (typically <c>A</c>).</summary>
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

    /// <summary>
    /// Merchant address for legal receipts: DB deployment override (operator-configured site),
    /// then MRA terminal config, then appsettings <see cref="TerminalDeploymentOptions.MerchantAddressLines"/>,
    /// then site/branch labels.
    /// </summary>
    public IReadOnlyList<string> AddressLines
    {
        get
        {
            var fromDb = DeploymentMerchantAddressLines?
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .ToList();
            if (fromDb is { Count: > 0 })
            {
                return fromDb;
            }

            var fromTerminal = Terminal?.AddressLines?
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .ToList();
            if (fromTerminal is { Count: > 0 })
            {
                return fromTerminal;
            }

            var fromOptions = Deployment?.MerchantAddressLines?
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .ToList();
            if (fromOptions is { Count: > 0 })
            {
                return fromOptions;
            }

            var siteName = PosConfigurationService.NormalizeConfiguredValue(Terminal?.TerminalSite?.SiteName);
            if (siteName is not null)
            {
                return [siteName];
            }

            if (!string.IsNullOrWhiteSpace(BranchId))
            {
                return [BranchId];
            }

            return Array.Empty<string>();
        }
    }

    /// <summary>Merchant phone for legal receipts (terminal config → DB → appsettings).</summary>
    public string? ContactPhone =>
        PosConfigurationService.NormalizeConfiguredValue(Terminal?.PhoneNumber)
        ?? PosConfigurationService.NormalizeConfiguredValue(DeploymentContactPhone)
        ?? PosConfigurationService.NormalizeConfiguredValue(Deployment?.ContactPhone);

    /// <summary>Merchant email for legal receipts (terminal config → DB → appsettings).</summary>
    public string? ContactEmail =>
        PosConfigurationService.NormalizeConfiguredValue(Terminal?.EmailAddress)
        ?? PosConfigurationService.NormalizeConfiguredValue(DeploymentContactEmail)
        ?? PosConfigurationService.NormalizeConfiguredValue(Deployment?.ContactEmail);
}
