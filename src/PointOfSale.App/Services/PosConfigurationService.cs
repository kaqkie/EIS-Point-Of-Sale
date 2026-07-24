using System.Text.Json;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.Services;

public interface IPosConfigurationService
{
    Task<PosRuntimeContext> GetRuntimeContextAsync(CancellationToken cancellationToken = default);

    string? GetConfiguredActivationCode();
}

public sealed class PosConfigurationService : IPosConfigurationService
{
    /// <summary>Historical sandbox seed — never treat as a real registered TIN on receipts.</summary>
    public const string SandboxPlaceholderTaxpayerTin = "1234567890";

    private readonly IConfigurationRepository _configurationRepository;
    private readonly TerminalDeploymentOptions _deployment;

    public PosConfigurationService(
        IConfigurationRepository configurationRepository,
        IOptions<TerminalDeploymentOptions> deployment)
    {
        _configurationRepository = configurationRepository;
        _deployment = deployment.Value;
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
            branchOverride);
    }

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
    /// Returns a usable taxpayer TIN, skipping unresolved templates and the sandbox placeholder.
    /// </summary>
    public static string? NormalizeTaxpayerTin(string? value)
    {
        var normalized = NormalizeConfiguredValue(value);
        if (normalized is null || IsPlaceholderTaxpayerTin(normalized))
        {
            return null;
        }

        return normalized;
    }

    /// <summary>True for the historical sandbox seed TIN that must not print on legal receipts.</summary>
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
    string? DeploymentBranchId = null)
{
    public string TradingName =>
        Terminal?.TradingName
        ?? Deployment?.FallbackTradingName
        ?? "Albert Retail Terminal";

    /// <summary>
    /// Seller TIN for checkout + legal receipts — prefers live MRA taxpayer config, then SQL
    /// deployment override, then <see cref="TerminalDeploymentOptions.TaxpayerTin"/>.
    /// Never returns the sandbox placeholder <c>1234567890</c>.
    /// </summary>
    public string SellerTin =>
        PosConfigurationService.NormalizeTaxpayerTin(Taxpayer?.Tin)
        ?? PosConfigurationService.NormalizeTaxpayerTin(DeploymentTaxpayerTin)
        ?? PosConfigurationService.NormalizeTaxpayerTin(Deployment?.TaxpayerTin)
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

    public int GlobalConfigVersion => Global?.VersionNo ?? 0;
    public int TerminalConfigVersion => Terminal?.VersionNo ?? 0;
    public int TaxpayerConfigVersion => Taxpayer?.VersionNo ?? 0;
    public IReadOnlyList<string> AddressLines => Terminal?.AddressLines ?? Array.Empty<string>();
}
