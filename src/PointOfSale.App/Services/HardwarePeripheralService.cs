using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;

namespace PointOfSale.App.Services;

public interface IHardwarePeripheralService : IAsyncDisposable
{
    event EventHandler<string>? BarcodeScanned;
    event EventHandler? PeripheralStatusChanged;

    bool IsPrinterConnected { get; }
    bool IsCashDrawerReady { get; }
    string ScannerStatus { get; }
    DateTime? LastPeripheralCheckTimestamp { get; }
    string? LastError { get; }

    Task<HardwarePeripheralHealthSnapshot> ProbeAsync(CancellationToken cancellationToken = default);
    Task PrintTestReceiptAsync(CancellationToken cancellationToken = default);
    Task PrintFiscalReceiptAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default);
    Task KickCashDrawerAsync(CancellationToken cancellationToken = default);
    Task StartScannerMonitoringAsync(CancellationToken cancellationToken = default);
    Task StopScannerMonitoringAsync();
    Task ReconnectAllAsync(CancellationToken cancellationToken = default);
}

public sealed class HardwarePeripheralHealthSnapshot
{
    public bool IsPrinterConnected { get; init; }
    public bool IsCashDrawerReady { get; init; }
    public string ScannerStatus { get; init; } = string.Empty;
    public DateTime CheckedAtUtc { get; init; }
    public string? LastError { get; init; }
    public int ConsecutivePrinterFailures { get; init; }
}

/// <summary>
/// Hardware abstraction for ESC/POS thermal printers (auto-cut + high-density MRA QR),
/// USB/COM barcode scanners, and cash-drawer kick — with reconnect during active checkout.
/// </summary>
public sealed class HardwarePeripheralService : IHardwarePeripheralService
{
    private readonly HardwarePeripheralOptions _options;
    private readonly ThermalPrinterOptions _thermal;
    private readonly IThermalPrinterHardwareService _thermalPrinter;
    private readonly IMraReceiptLayoutService _layoutService;
    private readonly ILogger<HardwarePeripheralService> _logger;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly object _scannerSync = new();

    private SerialPort? _scannerPort;
    private StringBuilder _scannerBuffer = new();
    private int _consecutivePrinterFailures;
    private bool _isPrinterConnected;
    private bool _isCashDrawerReady;
    private string _scannerStatus = "Idle";
    private DateTime? _lastCheckUtc;
    private string? _lastError;
    private bool _disposed;

    public HardwarePeripheralService(
        IOptions<HardwarePeripheralOptions> options,
        IOptions<ThermalPrinterOptions> thermal,
        IThermalPrinterHardwareService thermalPrinter,
        IMraReceiptLayoutService layoutService,
        ILogger<HardwarePeripheralService> logger)
    {
        _options = options.Value;
        _thermal = thermal.Value;
        _thermalPrinter = thermalPrinter;
        _layoutService = layoutService;
        _logger = logger;
        _isCashDrawerReady = _options.CashDrawerEnabled && _thermal.Enabled;
        _scannerStatus = _options.ScannerEnabled ? "Stopped" : "Disabled";
    }

    public event EventHandler<string>? BarcodeScanned;
    public event EventHandler? PeripheralStatusChanged;

    public bool IsPrinterConnected => _isPrinterConnected;
    public bool IsCashDrawerReady => _isCashDrawerReady;
    public string ScannerStatus => _scannerStatus;
    public DateTime? LastPeripheralCheckTimestamp => _lastCheckUtc;
    public string? LastError => _lastError;

    public async Task<HardwarePeripheralHealthSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _lastCheckUtc = DateTime.UtcNow;
            if (!_options.Enabled)
            {
                _isPrinterConnected = false;
                _isCashDrawerReady = false;
                _scannerStatus = "Disabled";
                RaiseStatus();
                return Snapshot();
            }

            try
            {
                if (_thermal.Enabled)
                {
                    // Lightweight presence probe: empty RAW job would fail printers; use spooler/serial open semantics.
                    if (_thermal.ConnectionMode == ThermalPrinterConnectionMode.Serial)
                    {
                        ProbeSerialPort(_thermal.SerialPortName, _thermal.BaudRate);
                    }

                    _isPrinterConnected = true;
                    _consecutivePrinterFailures = 0;
                    _lastError = null;
                }
                else
                {
                    _isPrinterConnected = false;
                    _lastError = "Thermal printer is disabled in configuration.";
                }
            }
            catch (Exception ex)
            {
                _isPrinterConnected = false;
                _consecutivePrinterFailures++;
                _lastError = ex.Message;
                _logger.LogWarning(ex, "Peripheral printer probe failed.");
            }

            _isCashDrawerReady = _options.CashDrawerEnabled && _isPrinterConnected;

            if (_options.ScannerEnabled)
            {
                lock (_scannerSync)
                {
                    _scannerStatus = _scannerPort is { IsOpen: true }
                        ? "Listening"
                        : "Not connected";
                }
            }
            else
            {
                _scannerStatus = "Disabled";
            }

            RaiseStatus();
            return Snapshot();
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task PrintTestReceiptAsync(CancellationToken cancellationToken = default)
    {
        var payload = EscPosReceiptEncoder.BuildHardwareTestPage(
            _thermal.CharactersPerLineResolved,
            _options.TestVerificationUrl);
        await ExecuteWithReconnectAsync(
                () => _thermalPrinter.PrintRawAsync(payload, "ART Hardware Test", cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        _isCashDrawerReady = _options.CashDrawerEnabled;
        RaiseStatus();
    }

    public async Task PrintFiscalReceiptAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = EscPosReceiptEncoder.Encode(
            request,
            _thermal.CharactersPerLineResolved,
            highDensityMraQr: _options.PreferHighDensityMraQr,
            layoutService: _layoutService);
        await ExecuteWithReconnectAsync(
                () => _thermalPrinter.PrintRawAsync(payload, "Albert Retail Terminal Receipt", cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task KickCashDrawerAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.CashDrawerEnabled)
        {
            throw new InvalidOperationException("Cash drawer peripheral is disabled in configuration.");
        }

        var kick = EscPosReceiptEncoder.BuildCashDrawerKick(_options.CashDrawerPin);
        await ExecuteWithReconnectAsync(
                () => _thermalPrinter.PrintRawAsync(kick, "ART Cash Drawer Kick", cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        _isCashDrawerReady = true;
        _lastCheckUtc = DateTime.UtcNow;
        RaiseStatus();
    }

    public Task StartScannerMonitoringAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.ScannerEnabled)
        {
            _scannerStatus = "Disabled";
            RaiseStatus();
            return Task.CompletedTask;
        }

        lock (_scannerSync)
        {
            if (_scannerPort is { IsOpen: true })
            {
                _scannerStatus = "Listening";
                RaiseStatus();
                return Task.CompletedTask;
            }

            try
            {
                OpenScannerPort();
                _scannerStatus = "Listening";
                _lastError = null;
            }
            catch (Exception ex)
            {
                _scannerStatus = "Error";
                _lastError = ex.Message;
                _logger.LogWarning(ex, "Failed to open barcode scanner on {Port}.", _options.ScannerPortName);
            }
        }

        RaiseStatus();
        return Task.CompletedTask;
    }

    public Task StopScannerMonitoringAsync()
    {
        lock (_scannerSync)
        {
            CloseScannerPort();
            _scannerStatus = _options.ScannerEnabled ? "Stopped" : "Disabled";
        }

        RaiseStatus();
        return Task.CompletedTask;
    }

    public async Task ReconnectAllAsync(CancellationToken cancellationToken = default)
    {
        await StopScannerMonitoringAsync().ConfigureAwait(false);
        _consecutivePrinterFailures = 0;
        await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (_options.ScannerEnabled)
        {
            await StartScannerMonitoringAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopScannerMonitoringAsync().ConfigureAwait(false);
        _ioGate.Dispose();
    }

    private async Task ExecuteWithReconnectAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            throw new InvalidOperationException("Hardware peripherals are disabled.");
        }

        if (!_thermal.Enabled)
        {
            throw new InvalidOperationException("Thermal printer is disabled; cannot send ESC/POS commands.");
        }

        var attempts = Math.Max(1, _options.MaxReconnectAttempts);
        Exception? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await action().ConfigureAwait(false);
                _isPrinterConnected = true;
                _consecutivePrinterFailures = 0;
                _lastError = null;
                _lastCheckUtc = DateTime.UtcNow;
                RaiseStatus();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                _isPrinterConnected = false;
                _isCashDrawerReady = false;
                _consecutivePrinterFailures++;
                _lastError = ex.Message;
                _logger.LogWarning(
                    ex,
                    "Peripheral I/O failed (attempt {Attempt}/{Max}). Reconnecting…",
                    attempt,
                    attempts);
                RaiseStatus();

                if (attempt < attempts)
                {
                    await Task.Delay(
                            Math.Max(100, _options.ReconnectDelayMs) * attempt,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException(
            $"Hardware peripheral failed after {attempts} reconnect attempt(s): {last?.Message}",
            last);
    }

    private void ProbeSerialPort(string portName, int baudRate)
    {
        if (string.IsNullOrWhiteSpace(portName))
        {
            throw new InvalidOperationException("Serial port name is not configured.");
        }

        using var port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = 2000,
            ReadTimeout = 500
        };
        port.Open();
        port.Close();
    }

    private void OpenScannerPort()
    {
        CloseScannerPort();
        _scannerBuffer = new StringBuilder();
        var port = new SerialPort(
            _options.ScannerPortName,
            _options.ScannerBaudRate,
            Parity.None,
            8,
            StopBits.One)
        {
            Handshake = Handshake.None,
            Encoding = Encoding.ASCII,
            NewLine = "\r",
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        port.DataReceived += OnScannerDataReceived;
        port.ErrorReceived += OnScannerErrorReceived;
        port.Open();
        _scannerPort = port;
    }

    private void CloseScannerPort()
    {
        if (_scannerPort is null)
        {
            return;
        }

        try
        {
            _scannerPort.DataReceived -= OnScannerDataReceived;
            _scannerPort.ErrorReceived -= OnScannerErrorReceived;
            if (_scannerPort.IsOpen)
            {
                _scannerPort.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scanner port close ignored.");
        }
        finally
        {
            _scannerPort.Dispose();
            _scannerPort = null;
        }
    }

    private void OnScannerDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            SerialPort? port;
            lock (_scannerSync)
            {
                port = _scannerPort;
            }

            if (port is null || !port.IsOpen)
            {
                return;
            }

            var chunk = port.ReadExisting();
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            foreach (var ch in chunk)
            {
                if (ch is '\r' or '\n')
                {
                    var code = _scannerBuffer.ToString().Trim();
                    _scannerBuffer.Clear();
                    if (code.Length > 0)
                    {
                        BarcodeScanned?.Invoke(this, code);
                    }
                }
                else if (!char.IsControl(ch))
                {
                    _scannerBuffer.Append(ch);
                    if (_scannerBuffer.Length > 128)
                    {
                        _scannerBuffer.Clear();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Scanner data read failed.");
            _scannerStatus = "Error";
            _lastError = ex.Message;
            RaiseStatus();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_options.ReconnectDelayMs).ConfigureAwait(false);
                    await StartScannerMonitoringAsync().ConfigureAwait(false);
                }
                catch (Exception reconnectEx)
                {
                    _logger.LogDebug(reconnectEx, "Scanner auto-reconnect failed.");
                }
            });
        }
    }

    private void OnScannerErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        _scannerStatus = "Error";
        _lastError = $"Scanner serial error: {e.EventType}";
        RaiseStatus();
    }

    private HardwarePeripheralHealthSnapshot Snapshot() => new()
    {
        IsPrinterConnected = _isPrinterConnected,
        IsCashDrawerReady = _isCashDrawerReady,
        ScannerStatus = _scannerStatus,
        CheckedAtUtc = _lastCheckUtc ?? DateTime.UtcNow,
        LastError = _lastError,
        ConsecutivePrinterFailures = _consecutivePrinterFailures
    };

    private void RaiseStatus() => PeripheralStatusChanged?.Invoke(this, EventArgs.Empty);
}
