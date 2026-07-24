namespace PointOfSale.Mra.Options;

public enum MraEisEnvironment
{
    Sandbox,
    Production
}

public sealed class MraApiOptions
{
    public const string SectionName = "MraEis";

    /// <summary>Official sandbox EIS API (reachable).</summary>
    public const string DefaultSandboxBaseUrl = "https://dev-eis-api.mra.mw/api/v1/";

    /// <summary>
    /// Live EIS API host. Prefer eis-api.mra.mw — legacy apis.mra.mw often fails DNS resolution.
    /// </summary>
    public const string DefaultProductionBaseUrl = "https://eis-api.mra.mw/api/v1/";

    public const string DefaultSandboxVerificationBaseUrl = "https://dev-eis-portal.mra.mw/verify";
    public const string DefaultProductionVerificationBaseUrl = "https://eis-portal.mra.mw/verify";

    /// <summary>Sandbox | Production — selects SandboxBaseUrl or ProductionBaseUrl when BaseUrl is not overridden.</summary>
    public string Environment { get; set; } = "Sandbox";

    public string SandboxBaseUrl { get; set; } = DefaultSandboxBaseUrl;

    public string ProductionBaseUrl { get; set; } = DefaultProductionBaseUrl;

    /// <summary>
    /// Explicit override. When empty, <see cref="Environment"/> selects Sandbox vs Production base URL.
    /// Avoid pointing this at unreachable hosts (e.g. historical apis.mra.mw).
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string ProductId { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = "1.0.0";

    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Optional integer form used in appsettings (PostConfigure maps this onto HttpTimeout).</summary>
    public int HttpTimeoutSeconds { get; set; }

    /// <summary>
    /// When true, TLS name/chain errors are accepted (intended for Sandbox / lab gateways).
    /// Defaults to true in Sandbox when unset via <see cref="ShouldRelaxServerCertificateValidation"/>.
    /// </summary>
    public bool? AllowInvalidServerCertificates { get; set; }

    public string VerificationBaseUrl { get; set; } = string.Empty;

    public string ResolveBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl) && !IsLegacyUnreachableHost(BaseUrl))
        {
            return NormalizeBaseUrl(BaseUrl);
        }

        return Environment.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? NormalizeBaseUrl(ProductionBaseUrl)
            : NormalizeBaseUrl(SandboxBaseUrl);
    }

    public string ResolveVerificationBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(VerificationBaseUrl) && !IsLegacyUnreachableHost(VerificationBaseUrl))
        {
            return VerificationBaseUrl.Trim().TrimEnd('/');
        }

        return Environment.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? DefaultProductionVerificationBaseUrl
            : DefaultSandboxVerificationBaseUrl;
    }

    public bool ShouldRelaxServerCertificateValidation()
    {
        if (AllowInvalidServerCertificates is bool explicitValue)
        {
            return explicitValue;
        }

        // Default: relax only for Sandbox so lab certs do not block fiscal sync.
        return !Environment.Equals("Production", StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return DefaultSandboxBaseUrl;
        }

        var trimmed = url.Trim();
        if (IsLegacyUnreachableHost(trimmed))
        {
            // Rewrite known-bad historical production host to the reachable EIS API.
            return DefaultProductionBaseUrl;
        }

        return trimmed.TrimEnd('/') + "/";
    }

    public static string NormalizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return string.Empty;
        }

        return relativePath.Trim().TrimStart('/');
    }

    /// <summary>
    /// Combines EIS base + relative path without dropping the /api/v1 segment.
    /// </summary>
    public static Uri CombineEndpoint(string baseUrl, string relativePath)
    {
        var normalizedBase = NormalizeBaseUrl(baseUrl);
        var relative = NormalizeRelativePath(relativePath);
        return new Uri(new Uri(normalizedBase, UriKind.Absolute), relative);
    }

    public static bool IsLegacyUnreachableHost(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && (url.Contains("apis.mra.mw", StringComparison.OrdinalIgnoreCase)
            || url.Contains("://eis.mra.mw", StringComparison.OrdinalIgnoreCase)
            || url.Contains("eis.mra.mw/verify", StringComparison.OrdinalIgnoreCase));
}
