namespace PointOfSale.Infrastructure.Options;

public sealed class OfflineSyncOptions
{
    public const string SectionName = "OfflineSync";

    public int PollIntervalSeconds { get; set; } = 15;

    public int MaxRetryAttempts { get; set; } = 8;

    public int BaseBackoffSeconds { get; set; } = 30;

    public int MaxBackoffSeconds { get; set; } = 3600;
}
