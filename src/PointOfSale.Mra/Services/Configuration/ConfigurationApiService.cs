using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Options;

namespace PointOfSale.Mra.Services.Configuration;

public sealed class ConfigurationApiService : Http.MraApiClientBase
{
    private const string GetLatestConfigsPath = "configuration/get-latest-configs";

    public ConfigurationApiService(
        HttpClient httpClient,
        IOptions<MraApiOptions> options,
        ILogger<ConfigurationApiService> logger)
        : base(httpClient, options, logger)
    {
    }

    public Task<EisApiResponse<GetLatestConfigurationResponseData>> GetLatestConfigurationAsync(
        string jwtToken,
        CancellationToken cancellationToken = default) =>
        GetAsync<GetLatestConfigurationResponseData>(GetLatestConfigsPath, jwtToken, cancellationToken);
}
