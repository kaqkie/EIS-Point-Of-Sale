using Microsoft.Extensions.DependencyInjection;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Services;
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

        services.AddSingleton<ILastSubmittedOfflineTransactionResponseService, LastSubmittedOfflineTransactionResponseService>();
        services.AddSingleton<ILastSubmittedOnlineTransactionResponseService, LastSubmittedOnlineTransactionResponseService>();
        services.AddHttpClient<OnboardingApiService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MraApiOptions>>().Value;
            if (Uri.TryCreate(MraApiOptions.NormalizeBaseUrl(opts.ResolveBaseUrl()), UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
            }

            client.Timeout = opts.HttpTimeout < TimeSpan.FromSeconds(30)
                ? TimeSpan.FromSeconds(30)
                : opts.HttpTimeout;
        });
        services.AddHttpClient<ConfigurationApiService>((sp, client) =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MraApiOptions>>().Value;
            if (Uri.TryCreate(MraApiOptions.NormalizeBaseUrl(opts.ResolveBaseUrl()), UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
            }

            client.Timeout = opts.HttpTimeout < TimeSpan.FromSeconds(30)
                ? TimeSpan.FromSeconds(30)
                : opts.HttpTimeout;
        });
        services.AddScoped<TerminalOnboardingService>();

        return services;
    }
}
