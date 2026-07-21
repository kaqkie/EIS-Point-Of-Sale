namespace PointOfSale.Core.Entities;

public sealed class ConfigurationEntry
{
    public required string ConfigKey { get; set; }
    public required string ConfigJson { get; set; }
    public DateTime UpdatedAt { get; set; }
}
