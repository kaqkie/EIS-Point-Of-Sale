using System.Net.Http;
using System.Net.Http.Headers;
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
/// All client property mutation (BaseAddress / Timeout / Accept) happens here once via IHttpClientFactory.
/// </summary>
public static class MraHttpClientFactory
{
    /// <summary>Floor for HttpClient.Timeout — prevents premature offline fallbacks on slow sandbox links.</summary>
    public const int MinimumTimeoutSeconds = 30;

    public static TimeSpan ResolveTimeout(MraApiOptions options)
    {
        var seconds = options.HttpTimeoutSeconds > 0
            ? options.HttpTimeoutSeconds
            : (int)Math.Ceiling(options.HttpTimeout.TotalSeconds);
        if (seconds <= 0)
        {
            seconds = MinimumTimeoutSeconds;
        }

        return TimeSpan.FromSeconds(Math.Max(MinimumTimeoutSeconds, seconds));
    }

    /// <summary>
    /// Creates the primary handler. Sandbox (or AllowInvalidServerCertificates) uses
    /// <see cref="HttpClientHandler.ServerCertificateCustomValidationCallback"/> = always true.
    /// </summary>
    public static HttpMessageHandler CreateHandler(MraApiOptions options)
    {
        if (options.ShouldRelaxServerCertificateValidation())
        {
            // Explicit HttpClientHandler path requested for sandbox cert bypass.
            return new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
                SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            };
        }

        var timeout = ResolveTimeout(options);
        return new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds, MinimumTimeoutSeconds, 60)),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = StrictCertificateValidation
            }
        };
    }

    /// <summary>
    /// One-time HttpClient property configuration for IHttpClientFactory.ConfigureHttpClient.
    /// Must not be called again after the client has started sending requests.
    /// </summary>
    public static void ConfigureClient(HttpClient client, MraApiOptions options, string? effectiveBaseUrl = null)
    {
        // Always enforce ≥30s timeout (spec: TimeSpan.FromSeconds(30) floor).
        client.Timeout = ResolveTimeout(options);

        var baseUrl = MraApiOptions.NormalizeBaseUrl(effectiveBaseUrl ?? options.ResolveBaseUrl());
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            client.BaseAddress = baseUri;
        }

        // Set Accept once during factory init — never from typed-client constructors after send.
        if (!client.DefaultRequestHeaders.Accept.Any(h =>
                h.MediaType?.Equals("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        if (!client.DefaultRequestHeaders.Accept.Any(h =>
                h.MediaType?.Equals("text/plain", StringComparison.OrdinalIgnoreCase) == true))
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
        }
    }

    private static bool StrictCertificateValidation(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors sslPolicyErrors) =>
        certificate is not null && sslPolicyErrors == SslPolicyErrors.None;
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

            // Sandbox default: always relax TLS validation unless explicitly forced off.
            if (options.AllowInvalidServerCertificates is null
                && !options.Environment.Equals("Production", StringComparison.OrdinalIgnoreCase))
            {
                options.AllowInvalidServerCertificates = true;
            }
        });
    }
}
