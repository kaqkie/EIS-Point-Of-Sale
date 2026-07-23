using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace PointOfSale.App.Services;

/// <summary>
/// Builds ESC/POS byte streams for 58mm/80mm thermal receipts including QR verification codes.
/// </summary>
public static class EscPosReceiptEncoder
{
    static EscPosReceiptEncoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        PrinterEncoding = Encoding.GetEncoding(437);
    }

    private static readonly Encoding PrinterEncoding;

    public static byte[] Encode(ReceiptPrintRequest request, int charactersPerLine, bool highDensityMraQr = false)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (charactersPerLine < 24)
        {
            charactersPerLine = 24;
        }

        using var buffer = new MemoryStream(2048);
        Write(buffer, Init());
        Write(buffer, AlignCenter());
        Write(buffer, Bold(true));
        WriteLine(buffer, Truncate(request.TradingName, charactersPerLine));
        Write(buffer, Bold(false));

        foreach (var address in request.AddressLines.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            WriteLine(buffer, Truncate(address, charactersPerLine));
        }

        WriteLine(buffer, Truncate($"TIN: {request.SellerTin}", charactersPerLine));
        Write(buffer, AlignLeft());
        WriteLine(buffer, Separator(charactersPerLine));
        WriteLine(buffer, Truncate($"Invoice: {request.InvoiceNumber}", charactersPerLine));
        WriteLine(buffer, Truncate($"Date: {request.InvoiceDateTime:yyyy-MM-dd HH:mm}", charactersPerLine));
        WriteLine(buffer, Separator(charactersPerLine));

        foreach (var item in request.LineItems)
        {
            WriteLine(buffer, Truncate(item.Description, charactersPerLine));
            var detail = $"{item.Quantity:N2} x {item.UnitPrice:N2}";
            var amount = $"{item.Total:N2}";
            WriteLine(buffer, Columns(detail, amount, charactersPerLine));
            WriteLine(buffer, Truncate($"  VAT {item.TotalVat:N2}", charactersPerLine));
        }

        WriteLine(buffer, Separator(charactersPerLine));
        foreach (var tax in request.TaxBreakdown)
        {
            WriteLine(buffer, Columns($"Tax {tax.RateId}", $"{tax.TaxAmount:N2}", charactersPerLine));
            WriteLine(buffer, Truncate($"  Taxable {tax.TaxableAmount:N2}", charactersPerLine));
        }

        WriteLine(buffer, Columns("Subtotal", $"{request.ResolveSubtotalNet():N2}", charactersPerLine));
        WriteLine(buffer, Columns("VAT 17.5%", $"{request.ResolveTotalVat():N2}", charactersPerLine));
        Write(buffer, Bold(true));
        WriteLine(buffer, Columns("TOTAL", $"{request.InvoiceTotal:N2}", charactersPerLine));
        Write(buffer, Bold(false));
        WriteLine(buffer, Columns("Tendered", $"{request.AmountTendered:N2}", charactersPerLine));
        WriteLine(buffer, Columns("Change", $"{request.ChangeDue:N2}", charactersPerLine));

        WriteLine(buffer, Separator(charactersPerLine));
        var fiscalSignature = request.FiscalResponse?.ResolveFiscalSignature() ?? string.Empty;
        var verificationUrl = request.FiscalResponse?.VerificationUrl ?? string.Empty;

        Write(buffer, AlignCenter());
        WriteLine(buffer, "MRA EIS Fiscal Signature");
        Write(buffer, AlignLeft());
        foreach (var chunk in Chunk(fiscalSignature, charactersPerLine))
        {
            WriteLine(buffer, chunk);
        }

        if (!string.IsNullOrWhiteSpace(verificationUrl))
        {
            Write(buffer, AlignCenter());
            WriteLine(buffer, "Scan to verify");
            Write(buffer, highDensityMraQr
                ? BuildHighDensityQrCode(verificationUrl)
                : BuildQrCode(verificationUrl));
            WriteLine(buffer, string.Empty);
            Write(buffer, AlignLeft());
            foreach (var chunk in Chunk(verificationUrl, charactersPerLine))
            {
                WriteLine(buffer, chunk);
            }
        }

        Write(buffer, AlignCenter());
        WriteLine(buffer, "Thank you");
        WriteLine(buffer, "Albert Retail Terminal");
        Write(buffer, FeedAndCut());
        return buffer.ToArray();
    }

    /// <summary>
    /// ESC/POS QR Code: Model 2, module size 4, error correction M.
    /// </summary>
    public static byte[] BuildQrCode(string data) => BuildQrCode(data, moduleSize: 4, errorCorrectionLevel: 'M');

    /// <summary>
    /// High-density MRA verification QR (larger modules + level H) for thermal scanners under store lighting.
    /// </summary>
    public static byte[] BuildHighDensityQrCode(string data) =>
        BuildQrCode(data, moduleSize: 8, errorCorrectionLevel: 'H');

    /// <summary>
    /// ESC/POS QR Code: Model 2 with configurable module size (1–16) and ECC L/M/Q/H.
    /// </summary>
    public static byte[] BuildQrCode(string data, int moduleSize, char errorCorrectionLevel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        moduleSize = Math.Clamp(moduleSize, 1, 16);
        var ecc = errorCorrectionLevel switch
        {
            'L' or 'l' => (byte)0x30,
            'Q' or 'q' => (byte)0x32,
            'H' or 'h' => (byte)0x33,
            _ => (byte)0x31 // M
        };

        var payload = Encoding.UTF8.GetBytes(data);
        using var ms = new MemoryStream(payload.Length + 64);

        // Model 2
        ms.Write([0x1D, 0x28, 0x6B, 0x04, 0x00, 0x31, 0x41, 0x32, 0x00]);
        // Module size
        ms.Write([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x43, (byte)moduleSize]);
        // Error correction
        ms.Write([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x45, ecc]);

        var storeLen = payload.Length + 3;
        ms.WriteByte(0x1D);
        ms.WriteByte(0x28);
        ms.WriteByte(0x6B);
        Span<byte> len = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(len, (ushort)storeLen);
        ms.Write(len);
        ms.WriteByte(0x31);
        ms.WriteByte(0x50);
        ms.WriteByte(0x30);
        ms.Write(payload);

        // Print
        ms.Write([0x1D, 0x28, 0x6B, 0x03, 0x00, 0x31, 0x51, 0x30]);
        return ms.ToArray();
    }

    /// <summary>
    /// Cash drawer kick pulse (ESC p): pin 2, ~50ms on / ~250ms off — standard Epson-compatible drawer.
    /// </summary>
    public static byte[] BuildCashDrawerKick(byte pin = 0, byte onTime = 0x19, byte offTime = 0xFA) =>
        [0x1B, 0x70, pin, onTime, offTime];

    /// <summary>
    /// Full cut after feed (GS V 0) — auto-cutter on Epson / Star / Bixolon class printers.
    /// </summary>
    public static byte[] BuildAutoCut() => [0x0A, 0x0A, 0x0A, 0x1D, 0x56, 0x00];

    /// <summary>Supervisor diagnostic test page with high-density sample QR and auto-cut.</summary>
    public static byte[] BuildHardwareTestPage(int charactersPerLine, string verificationSampleUrl)
    {
        if (charactersPerLine < 24)
        {
            charactersPerLine = 24;
        }

        using var buffer = new MemoryStream(512);
        Write(buffer, Init());
        Write(buffer, AlignCenter());
        Write(buffer, Bold(true));
        WriteLine(buffer, Truncate("Albert Retail Terminal", charactersPerLine));
        Write(buffer, Bold(false));
        WriteLine(buffer, Truncate("Hardware peripheral test", charactersPerLine));
        Write(buffer, AlignLeft());
        WriteLine(buffer, Separator(charactersPerLine));
        WriteLine(buffer, Truncate($"Printed: {DateTime.Now:yyyy-MM-dd HH:mm:ss}", charactersPerLine));
        WriteLine(buffer, Truncate("ESC/POS · auto-cut · MRA QR", charactersPerLine));
        Write(buffer, AlignCenter());
        WriteLine(buffer, "High-density MRA QR");
        Write(buffer, BuildHighDensityQrCode(
            string.IsNullOrWhiteSpace(verificationSampleUrl)
                ? "https://eis.mra.mw/verify"
                : verificationSampleUrl));
        WriteLine(buffer, string.Empty);
        Write(buffer, AlignCenter());
        WriteLine(buffer, "Cut below");
        Write(buffer, BuildAutoCut());
        return buffer.ToArray();
    }

    private static byte[] Init() => [0x1B, 0x40];
    private static byte[] AlignLeft() => [0x1B, 0x61, 0x00];
    private static byte[] AlignCenter() => [0x1B, 0x61, 0x01];
    private static byte[] Bold(bool on) => [0x1B, 0x45, (byte)(on ? 1 : 0)];
    private static byte[] FeedAndCut() => BuildAutoCut();

    private static void Write(Stream stream, byte[] data) => stream.Write(data, 0, data.Length);

    private static void WriteLine(Stream stream, string text)
    {
        var bytes = PrinterEncoding.GetBytes(text + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Separator(int width) => new('-', Math.Min(width, 64));

    private static string Columns(string left, string right, int width)
    {
        left = Truncate(left, Math.Max(1, width - right.Length - 1));
        var spaces = Math.Max(1, width - left.Length - right.Length);
        return left + new string(' ', spaces) + right;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static IEnumerable<string> Chunk(string value, int size)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        for (var i = 0; i < value.Length; i += size)
        {
            yield return value.Substring(i, Math.Min(size, value.Length - i));
        }
    }
}
