namespace PointOfSale.App.Options;

public enum ThermalPrinterConnectionMode
{
    Spooler,
    Serial
}

/// <summary>
/// ESC/POS thermal printer pairing for 58mm / 80mm receipt paper.
/// </summary>
public sealed class ThermalPrinterOptions
{
    public const string SectionName = "ThermalPrinter";

    public bool Enabled { get; set; }

    public ThermalPrinterConnectionMode ConnectionMode { get; set; } = ThermalPrinterConnectionMode.Spooler;

    /// <summary>58 or 80 mm paper width.</summary>
    public int PaperWidthMm { get; set; } = 80;

    /// <summary>Windows printer queue name; empty = default printer.</summary>
    public string PrinterName { get; set; } = string.Empty;

    public string SerialPortName { get; set; } = "COM3";

    public int BaudRate { get; set; } = 9600;

    /// <summary>Characters per line; 32 typical for 58mm, 48 for 80mm.</summary>
    public int CharactersPerLine { get; set; } = 48;

    /// <summary>When false, fall back to WPF FlowDocument spooler print.</summary>
    public bool PreferEscPos { get; set; } = true;

    public int CharactersPerLineResolved =>
        CharactersPerLine > 0
            ? CharactersPerLine
            : PaperWidthMm <= 58 ? 32 : 48;
}
