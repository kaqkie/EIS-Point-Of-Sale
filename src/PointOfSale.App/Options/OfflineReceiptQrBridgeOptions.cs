namespace PointOfSale.App.Options;

/// <summary>
/// Local LAN bridge for offline MRA ValidationURL QR scans.
/// The sandbox/production MRA <c>ReceiptValidation/Validate</c> portal currently returns
/// Internal Server Error; this host verifies the same HMAC locally so customer scans work
/// on the store Wi‑Fi until MRA restores their portal (and online fiscalization succeeds).
/// </summary>
public sealed class OfflineReceiptQrBridgeOptions
{
    public const string SectionName = "OfflineReceiptQrBridge";

    /// <summary>When true, offline ValidationURL QRs are rewritten to this till's LAN listener.</summary>
    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = 18787;

    /// <summary>Path prefix matching MRA's offline Validate route.</summary>
    public string Path { get; set; } = "/ReceiptValidation/Validate/";
}
