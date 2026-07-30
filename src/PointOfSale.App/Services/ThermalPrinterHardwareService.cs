using System.IO.Ports;
using System.Printing;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;

namespace PointOfSale.App.Services;

public interface IThermalPrinterHardwareService
{
    bool IsEnabled { get; }

    Task PrintReceiptAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default);

    Task PrintRawAsync(byte[] payload, string documentName, CancellationToken cancellationToken = default);

    byte[] BuildEscPosPayload(ReceiptPrintRequest request);
}

/// <summary>
/// Physical ESC/POS thermal printer communication via Windows spooler (RAW) or serial port.
/// </summary>
public sealed class ThermalPrinterHardwareService : IThermalPrinterHardwareService
{
    private readonly ThermalPrinterOptions _options;
    private readonly IMraReceiptLayoutService _layoutService;
    private readonly ILogger<ThermalPrinterHardwareService> _logger;

    public ThermalPrinterHardwareService(
        IOptions<ThermalPrinterOptions> options,
        IMraReceiptLayoutService layoutService,
        ILogger<ThermalPrinterHardwareService> logger)
    {
        _options = options.Value;
        _layoutService = layoutService;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled && _options.PreferEscPos;

    public byte[] BuildEscPosPayload(ReceiptPrintRequest request) =>
        EscPosReceiptEncoder.Encode(
            request,
            _options.CharactersPerLineResolved,
            highDensityMraQr: false,
            layoutService: _layoutService);

    public async Task PrintReceiptAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = BuildEscPosPayload(request);
        await PrintRawAsync(payload, "Albert Retail Terminal Receipt", cancellationToken).ConfigureAwait(false);
    }

    public async Task PrintRawAsync(byte[] payload, string documentName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length == 0)
        {
            throw new ArgumentException("ESC/POS payload is empty.", nameof(payload));
        }

        if (_options.ConnectionMode == ThermalPrinterConnectionMode.Serial)
        {
            await Task.Run(() => WriteSerial(payload), cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Run(() => WriteSpooler(payload, documentName), cancellationToken).ConfigureAwait(false);
    }

    private void WriteSerial(byte[] payload)
    {
        if (string.IsNullOrWhiteSpace(_options.SerialPortName))
        {
            throw new InvalidOperationException("Thermal printer serial port name is not configured.");
        }

        using var port = new SerialPort(
            _options.SerialPortName,
            _options.BaudRate,
            Parity.None,
            8,
            StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = 5000,
            ReadTimeout = 1000
        };

        port.Open();
        port.Write(payload, 0, payload.Length);
        port.BaseStream.Flush();
        _logger.LogInformation(
            "ESC/POS receipt sent to serial port {Port} ({Bytes} bytes).",
            _options.SerialPortName,
            payload.Length);
    }

    private void WriteSpooler(byte[] payload, string documentName = "Albert Retail Terminal Receipt")
    {
        var printerName = ResolvePrinterName();
        if (!RawPrinterHelper.SendBytesToPrinter(printerName, payload, documentName))
        {
            throw new InvalidOperationException(
                $"Failed to send ESC/POS data to Windows printer '{printerName}'.");
        }

        _logger.LogInformation(
            "ESC/POS job '{Document}' sent to spooler printer {Printer} ({Bytes} bytes, {Width}mm).",
            documentName,
            printerName,
            payload.Length,
            _options.PaperWidthMm);
    }

    private string ResolvePrinterName()
    {
        if (!string.IsNullOrWhiteSpace(_options.PrinterName))
        {
            return _options.PrinterName.Trim();
        }

        using var server = new LocalPrintServer();
        var queue = server.DefaultPrintQueue
            ?? throw new InvalidOperationException(
                "No default Windows printer is configured for thermal receipt printing.");
        return queue.FullName;
    }
}

/// <summary>
/// Win32 RAW print spooler helper for ESC/POS byte streams.
/// </summary>
internal static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class DocInfo1
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pDocName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pOutputFile;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pDatatype;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DocInfo1 di);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static bool SendBytesToPrinter(string printerName, byte[] bytes, string? documentName = null)
    {
        if (!OpenPrinter(printerName, out var printer, IntPtr.Zero))
        {
            return false;
        }

        var di = new DocInfo1
        {
            pDocName = string.IsNullOrWhiteSpace(documentName)
                ? "Albert Retail Terminal Receipt"
                : documentName,
            pDatatype = "RAW"
        };

        try
        {
            if (!StartDocPrinter(printer, 1, di))
            {
                return false;
            }

            try
            {
                if (!StartPagePrinter(printer))
                {
                    return false;
                }

                try
                {
                    var unmanaged = Marshal.AllocHGlobal(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, unmanaged, bytes.Length);
                        if (!WritePrinter(printer, unmanaged, bytes.Length, out var written))
                        {
                            return false;
                        }

                        return written == bytes.Length;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(unmanaged);
                    }
                }
                finally
                {
                    EndPagePrinter(printer);
                }
            }
            finally
            {
                EndDocPrinter(printer);
            }
        }
        finally
        {
            ClosePrinter(printer);
        }
    }
}
