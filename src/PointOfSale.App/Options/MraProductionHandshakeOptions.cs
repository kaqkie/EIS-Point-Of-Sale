namespace PointOfSale.App.Options;

public sealed class MraProductionHandshakeOptions
{
    public const string SectionName = "MraProductionHandshake";

    public bool Enabled { get; set; } = true;

    public int CertificateWarningDays { get; set; } = 14;

    public int CertificateLockoutDays { get; set; } = 0;

    public int ValidationIntervalMinutes { get; set; } = 30;

    public int HttpTimeoutSeconds { get; set; } = 25;
}
