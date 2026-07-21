namespace PointOfSale.Mra.Options;

public sealed class MraApiOptions
{
    public const string SectionName = "MraEis";

    /// <summary>Dev: https://dev-eis-api.mra.mw/api/v1 — Production base URL from MRA.</summary>
    public string BaseUrl { get; set; } = "https://dev-eis-api.mra.mw/api/v1/";

    public string ProductId { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = "1.0.0";

    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
