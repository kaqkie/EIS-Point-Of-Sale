namespace PointOfSale.Mra.Options;

public enum MraEisEnvironment
{
    Sandbox,
    Production
}

public sealed class MraApiOptions
{
    public const string SectionName = "MraEis";

    /// <summary>Sandbox | Production — selects SandboxBaseUrl or ProductionBaseUrl when BaseUrl is not overridden.</summary>
    public string Environment { get; set; } = "Sandbox";

    public string SandboxBaseUrl { get; set; } = "https://dev-eis-api.mra.mw/api/v1/";

    public string ProductionBaseUrl { get; set; } = "https://apis.mra.mw/api/v1/";

    /// <summary>Explicit override; when empty, Environment determines the active base URL.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string ProductId { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = "1.0.0";

    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public string ResolveBaseUrl()
    {
        if (!string.IsNullOrWhiteSpace(BaseUrl))
        {
            return BaseUrl.TrimEnd('/') + "/";
        }

        return Environment.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? ProductionBaseUrl.TrimEnd('/') + "/"
            : SandboxBaseUrl.TrimEnd('/') + "/";
    }
}
