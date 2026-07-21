using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Security;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Options;

namespace PointOfSale.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPointOfSaleInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<MraApiOptions>(configuration.GetSection(MraApiOptions.SectionName));
        services.PostConfigure<MraApiOptions>(options =>
        {
            var timeoutSeconds = configuration.GetValue<int?>("MraEis:HttpTimeoutSeconds");
            if (timeoutSeconds is > 0)
            {
                options.HttpTimeout = TimeSpan.FromSeconds(timeoutSeconds.Value);
            }
        });

        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();

        services.AddScoped<ITerminalRepository, TerminalRepository>();
        services.AddScoped<IConfigurationRepository, ConfigurationRepository>();
        services.AddScoped<IOfflineInvoiceQueueRepository, OfflineInvoiceQueueRepository>();
        services.AddScoped<ILocalInventoryRepository, LocalInventoryRepository>();

        services.AddHttpClient<MraApiClient>();
        services.AddScoped<IMraTerminalAuthProvider, MraTerminalAuthProvider>();
        services.AddScoped<TerminalOnboardingService>();
        services.AddScoped<StockManagementService>();

        return services;
    }
}
