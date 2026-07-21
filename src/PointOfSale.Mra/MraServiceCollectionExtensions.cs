using Microsoft.Extensions.DependencyInjection;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Services.Configuration;
using PointOfSale.Mra.Services.Onboarding;

namespace PointOfSale.Mra;

public static class MraServiceCollectionExtensions
{
    public static IServiceCollection AddMraEisIntegration(
        this IServiceCollection services,
        Action<MraApiOptions>? configureOptions = null)
    {
        if (configureOptions is not null)
        {
            services.Configure(configureOptions);
        }
        else
        {
            services.AddOptions<MraApiOptions>();
        }

        services.AddHttpClient<OnboardingApiService>();
        services.AddHttpClient<ConfigurationApiService>();
        services.AddScoped<TerminalOnboardingService>();

        return services;
    }
}
