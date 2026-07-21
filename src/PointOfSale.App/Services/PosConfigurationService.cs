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
        string.IsNullOrWhiteSpace(_deployment.TerminalActivationCode)
            ? null
            : _deployment.TerminalActivationCode.Trim();

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

        return new PosRuntimeContext(global, terminal, taxpayer, _deployment);
    }

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? default
            : JsonSerializer.Deserialize<T>(json, MraJson.SerializerOptions);
}

public sealed record PosRuntimeContext(
    GlobalConfigurationDto? Global,
    TerminalConfigurationDto? Terminal,
    TaxpayerConfigurationDto? Taxpayer,
    TerminalDeploymentOptions? Deployment = null)
{
    public string TradingName =>
        Terminal?.TradingName
        ?? Deployment?.FallbackTradingName
        ?? "Albert Retail Terminal";

    public string SellerTin => Taxpayer?.Tin ?? string.Empty;

    public string SiteId =>
        Terminal?.TerminalSite?.SiteId
        ?? Deployment?.SiteId
        ?? string.Empty;

    public string BranchId => Deployment?.BranchId ?? string.Empty;

    public int GlobalConfigVersion => Global?.VersionNo ?? 0;
    public int TerminalConfigVersion => Terminal?.VersionNo ?? 0;
    public int TaxpayerConfigVersion => Taxpayer?.VersionNo ?? 0;
    public IReadOnlyList<string> AddressLines => Terminal?.AddressLines ?? Array.Empty<string>();
}
