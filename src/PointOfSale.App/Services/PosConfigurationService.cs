using System.Text.Json;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.App.Services;

public interface IPosConfigurationService
{
    Task<PosRuntimeContext> GetRuntimeContextAsync(CancellationToken cancellationToken = default);
}

public sealed class PosConfigurationService : IPosConfigurationService
{
    private readonly IConfigurationRepository _configurationRepository;

    public PosConfigurationService(IConfigurationRepository configurationRepository)
    {
        _configurationRepository = configurationRepository;
    }

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

        return new PosRuntimeContext(global, terminal, taxpayer);
    }

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? default
            : JsonSerializer.Deserialize<T>(json, MraJson.SerializerOptions);
}

public sealed record PosRuntimeContext(
    GlobalConfigurationDto? Global,
    TerminalConfigurationDto? Terminal,
    TaxpayerConfigurationDto? Taxpayer)
{
    public string TradingName => Terminal?.TradingName ?? "Albert Retail Terminal";
    public string SellerTin => Taxpayer?.Tin ?? string.Empty;
    public string SiteId => Terminal?.TerminalSite?.SiteId ?? string.Empty;
    public int GlobalConfigVersion => Global?.VersionNo ?? 0;
    public int TerminalConfigVersion => Terminal?.VersionNo ?? 0;
    public int TaxpayerConfigVersion => Taxpayer?.VersionNo ?? 0;
    public IReadOnlyList<string> AddressLines => Terminal?.AddressLines ?? Array.Empty<string>();
}
