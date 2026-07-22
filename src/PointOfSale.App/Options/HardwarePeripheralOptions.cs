namespace PointOfSale.App.Options;

/// <summary>
/// Field hardware peripherals: thermal printer reconnection, cash drawer kick, USB/COM barcode scanner.
/// </summary>
public sealed class HardwarePeripheralOptions
{
    public const string SectionName = "HardwarePeripherals";

    public bool Enabled { get; set; } = true;

    /// <summary>Automatic reconnect attempts after a disconnect during checkout.</summary>
    public int MaxReconnectAttempts { get; set; } = 3;

    /// <summary>Delay between reconnect attempts (milliseconds).</summary>
    public int ReconnectDelayMs { get; set; } = 750;

    /// <summary>Periodic health probe interval while the diagnostics dashboard is open.</summary>
    public int HealthProbeIntervalSeconds { get; set; } = 20;

    /// <summary>When true, open the configured scanner serial port and raise barcode events.</summary>
    public bool ScannerEnabled { get; set; }

    /// <summary>COM port for USB-serial barcode scanners (keyboard-wedge scanners need no port).</summary>
    public string ScannerPortName { get; set; } = "COM4";

    public int ScannerBaudRate { get; set; } = 9600;

    /// <summary>Cash drawer kick pin (0 = pin 2, 1 = pin 5) via ESC/POS ESC p.</summary>
    public byte CashDrawerPin { get; set; }

    public bool CashDrawerEnabled { get; set; } = true;

    /// <summary>Use high-density QR (module 8, ECC H) for MRA verification URLs on fiscal receipts.</summary>
    public bool PreferHighDensityMraQr { get; set; } = true;

    /// <summary>Sample URL printed on hardware test pages.</summary>
    public string TestVerificationUrl { get; set; } = "https://eis.mra.mw/verify";
}
