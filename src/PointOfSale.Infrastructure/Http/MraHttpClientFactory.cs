using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PointOfSale.Mra.Options;

namespace PointOfSale.Infrastructure.Http;

/// <summary>
/// Shared HttpClient wiring for MRA EIS — base address, TLS 1.2/1.3, timeouts, optional sandbox cert leniency.
/// </summary>
public static class MraHttpClientFactory
{
    public const int MinimumTimeoutSeconds = 30;

    public static TimeSpan ResolveTimeout(MraApiOptions options)
    {
        var seconds = options.HttpTimeoutSeconds > 0
            ? options.HttpTimeoutSeconds
            : (int)Math.Ceiling(options.HttpTimeout.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(MinimumTimeoutSeconds, seconds <= 0 ? MinimumTimeoutSeconds : seconds));
    }

    public static SocketsHttpHandler CreateHandler(MraApiOptions options)
    {
        var timeout = ResolveTimeout(options);
        var handler = new SocketsHttpHandler
        {
            // Allow enough time for TLS + slow MRA sandbox links before treating as offline.
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds, MinimumTimeoutSeconds, 60)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }
        };

        // Sandbox / explicit opt-in: accept enterprise or incompletely chained certs so sync can proceed.
        // Production keeps strict validation unless AllowInvalidServerCertificates is forced on.
        if (options.ShouldRelaxServerCertificateValidation())
        {
            handler.SslOptions.RemoteCertificateValidationCallback = RelaxedCertificateValidation;
        }

        return handler;
    }

    public static void ConfigureClient(HttpClient client, MraApiOptions options, string? effectiveBaseUrl = null)
    {
        var timeout = ResolveTimeout(options);
        client.Timeout = timeout;

        var baseUrl = MraApiOptions.NormalizeBaseUrl(effectiveBaseUrl ?? options.ResolveBaseUrl());
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            client.BaseAddress = baseUri;
        }
    }

    private static bool RelaxedCertificateValidation(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors)
    {
        // Still require a certificate to be presented.
        if (certificate is null)
        {
            return false;
        }

        // Sandbox / lab gateways: accept name mismatch and incomplete chains.
        // Any other policy error is also accepted here because ShouldRelaxServerCertificateValidation
        // is only enabled for Sandbox (or explicit AllowInvalidServerCertificates).
        return true;
    }
}

public static class MraApiOptionsConfiguration
{
    public static void Apply(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MraApiOptions>(configuration.GetSection(MraApiOptions.SectionName));
        services.PostConfigure<MraApiOptions>(options =>
        {
            var timeoutSeconds = configuration.GetValue<int?>("MraEis:HttpTimeoutSeconds")
                ?? (options.HttpTimeoutSeconds > 0 ? options.HttpTimeoutSeconds : null);
            if (timeoutSeconds is > 0)
            {
                options.HttpTimeout = TimeSpan.FromSeconds(Math.Max(MraHttpClientFactory.MinimumTimeoutSeconds, timeoutSeconds.Value));
                options.HttpTimeoutSeconds = (int)options.HttpTimeout.TotalSeconds;
            }
            else
            {
                options.HttpTimeout = MraHttpClientFactory.ResolveTimeout(options);
                options.HttpTimeoutSeconds = (int)options.HttpTimeout.TotalSeconds;
            }

            options.SandboxBaseUrl = MraApiOptions.NormalizeBaseUrl(options.SandboxBaseUrl);
            options.ProductionBaseUrl = MraApiOptions.NormalizeBaseUrl(options.ProductionBaseUrl);
            if (!string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                options.BaseUrl = MraApiOptions.NormalizeBaseUrl(options.BaseUrl);
            }
        });
    }
}
