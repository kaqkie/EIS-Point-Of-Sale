using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Pricing;
using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.App.Services;

public interface IHardwareIntegrationService : IAsyncDisposable
{
    event EventHandler<string>? BarcodeScanned;
    event EventHandler? PeripheralStatusChanged;

    bool IsPrinterConnected { get; }
    bool IsCashDrawerReady { get; }
    string ScannerStatus { get; }
    DateTime? LastPeripheralCheckTimestamp { get; }
    string? LastError { get; }
    bool IsFaultToleranceLoopRunning { get; }

    Task<HardwarePeripheralHealthSnapshot> ProbeAsync(CancellationToken cancellationToken = default);
    Task PrintTestReceiptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Prints a statutory Malawi 17.5% VAT fiscal receipt with high-density MRA QR (ESC/POS).
    /// </summary>
    Task PrintStatutoryVatReceiptAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default);

    Task KickCashDrawerAsync(CancellationToken cancellationToken = default);
    Task StartScannerMonitoringAsync(CancellationToken cancellationToken = default);
    Task StopScannerMonitoringAsync();
    Task ReconnectAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a background probe/reconnect loop so cashier checkout survives transient USB/COM drops.
    /// </summary>
    Task StartFaultToleranceLoopAsync(CancellationToken cancellationToken = default);

    Task StopFaultToleranceLoopAsync();

    /// <summary>Normalizes USB/HID or serial scanner payloads into a clean product barcode.</summary>
    string DecodeScannerInput(string rawInput);

    /// <summary>Builds ESC/POS cash-drawer kick bytes (<c>ESC p</c>).</summary>
    byte[] BuildCashDrawerKickCommand(byte pin = 0);

    /// <summary>Builds high-density MRA verification QR ESC/POS payload.</summary>
    byte[] BuildHighDensityMraQrCommand(string verificationUrl);

    /// <summary>Encodes a full statutory VAT receipt including high-density QR and VAT banner.</summary>
    byte[] EncodeStatutoryVatReceipt(ReceiptPrintRequest request, int charactersPerLine);
}

/// <summary>
/// Production hardware integration façade: ESC/POS thermal (17.5% VAT + high-density MRA QR),
/// USB/COM barcode decode, cash-drawer <c>ESC p</c>, and automatic reconnection loops.
/// </summary>
public sealed class HardwareIntegrationService : IHardwareIntegrationService
{
    private static readonly Regex NonBarcodeNoise = new(
        @"[\u0000-\u001F\u007F]",
        RegexOptions.Compiled);

    private readonly IHardwarePeripheralService _peripherals;
    private readonly HardwarePeripheralOptions _options;
    private readonly ThermalPrinterOptions _thermal;
    private readonly ILogger<HardwareIntegrationService> _logger;
    private readonly object _loopSync = new();

    private CancellationTokenSource? _faultLoopCts;
    private Task? _faultLoopTask;
    private bool _disposed;

    public HardwareIntegrationService(
        IHardwarePeripheralService peripherals,
        IOptions<HardwarePeripheralOptions> options,
        IOptions<ThermalPrinterOptions> thermal,
        ILogger<HardwareIntegrationService> logger)
    {
        _peripherals = peripherals;
        _options = options.Value;
        _thermal = thermal.Value;
        _logger = logger;

        _peripherals.BarcodeScanned += OnPeripheralBarcodeScanned;
        _peripherals.PeripheralStatusChanged += OnPeripheralStatusChanged;
    }

    public event EventHandler<string>? BarcodeScanned;
    public event EventHandler? PeripheralStatusChanged;

    public bool IsPrinterConnected => _peripherals.IsPrinterConnected;
    public bool IsCashDrawerReady => _peripherals.IsCashDrawerReady;
    public string ScannerStatus => _peripherals.ScannerStatus;
    public DateTime? LastPeripheralCheckTimestamp => _peripherals.LastPeripheralCheckTimestamp;
    public string? LastError => _peripherals.LastError;

    public bool IsFaultToleranceLoopRunning
    {
        get
        {
            lock (_loopSync)
            {
                return _faultLoopTask is { IsCompleted: false };
            }
        }
    }

    public Task<HardwarePeripheralHealthSnapshot> ProbeAsync(CancellationToken cancellationToken = default) =>
        _peripherals.ProbeAsync(cancellationToken);

    public Task PrintTestReceiptAsync(CancellationToken cancellationToken = default) =>
        _peripherals.PrintTestReceiptAsync(cancellationToken);

    public async Task PrintStatutoryVatReceiptAsync(
        ReceiptPrintRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var annotated = AnnotateStatutoryVat(request);
        var attempts = Math.Max(1, _options.MaxReconnectAttempts);
        Exception? last = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _peripherals.PrintFiscalReceiptAsync(annotated, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                _logger.LogWarning(
                    ex,
                    "Statutory VAT receipt print attempt {Attempt}/{Max} failed.",
                    attempt,
                    attempts);
                await Task.Delay(
                        TimeSpan.FromMilliseconds(Math.Max(100, _options.ReconnectDelayMs)),
                        cancellationToken)
                    .ConfigureAwait(false);
                await _peripherals.ReconnectAllAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        throw last ?? new InvalidOperationException("Failed to print statutory VAT receipt.");
    }

    public Task KickCashDrawerAsync(CancellationToken cancellationToken = default) =>
        _peripherals.KickCashDrawerAsync(cancellationToken);

    public Task StartScannerMonitoringAsync(CancellationToken cancellationToken = default) =>
        _peripherals.StartScannerMonitoringAsync(cancellationToken);

    public Task StopScannerMonitoringAsync() => _peripherals.StopScannerMonitoringAsync();

    public Task ReconnectAllAsync(CancellationToken cancellationToken = default) =>
        _peripherals.ReconnectAllAsync(cancellationToken);

    public Task StartFaultToleranceLoopAsync(CancellationToken cancellationToken = default)
    {
        lock (_loopSync)
        {
            if (_faultLoopTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _faultLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _faultLoopCts.Token;
            _faultLoopTask = Task.Run(() => RunFaultToleranceLoopAsync(token), token);
        }

        return Task.CompletedTask;
    }

    public async Task StopFaultToleranceLoopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_loopSync)
        {
            cts = _faultLoopCts;
            loop = _faultLoopTask;
            _faultLoopCts = null;
            _faultLoopTask = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
            if (loop is not null)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on stop.
                }
            }
        }
        finally
        {
            cts.Dispose();
        }
    }

    public string DecodeScannerInput(string rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return string.Empty;
        }

        var cleaned = NonBarcodeNoise.Replace(rawInput, string.Empty).Trim();
        cleaned = cleaned.Trim('\r', '\n', '\t', ' ');

        // Strip AIM symbology identifiers such as "]C1" / "]E0".
        if (cleaned.Length > 3 && cleaned[0] == ']')
        {
            cleaned = cleaned[3..];
        }

        // GS1 AI(01) GTIN-14
        if (cleaned.StartsWith("01", StringComparison.Ordinal)
            && cleaned.Length >= 16
            && cleaned.AsSpan(2, 14).ToArray().All(char.IsDigit))
        {
            return cleaned.Substring(2, 14);
        }

        return cleaned;
    }

    public byte[] BuildCashDrawerKickCommand(byte pin = 0) =>
        EscPosReceiptEncoder.BuildCashDrawerKick(pin);

    public byte[] BuildHighDensityMraQrCommand(string verificationUrl) =>
        EscPosReceiptEncoder.BuildHighDensityQrCode(verificationUrl);

    public byte[] EncodeStatutoryVatReceipt(ReceiptPrintRequest request, int charactersPerLine)
    {
        ArgumentNullException.ThrowIfNull(request);
        var annotated = AnnotateStatutoryVat(request);
        var body = EscPosReceiptEncoder.Encode(annotated, charactersPerLine, highDensityMraQr: true);

        using var buffer = new MemoryStream(body.Length + 128);
        // ESC @ init
        buffer.WriteByte(0x1B);
        buffer.WriteByte(0x40);
        // Center + statutory VAT banner
        buffer.WriteByte(0x1B);
        buffer.WriteByte(0x61);
        buffer.WriteByte(0x01);
        var banner = Encoding.ASCII.GetBytes(
            $"VAT {PosTaxCalculator.MalawiStandardVatRatePercent:0.0}% STATUTORY\n");
        buffer.Write(banner);

        var offset = body.Length >= 2 && body[0] == 0x1B && body[1] == 0x40 ? 2 : 0;
        buffer.Write(body, offset, body.Length - offset);
        return buffer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _peripherals.BarcodeScanned -= OnPeripheralBarcodeScanned;
        _peripherals.PeripheralStatusChanged -= OnPeripheralStatusChanged;
        await StopFaultToleranceLoopAsync().ConfigureAwait(false);
    }

    private void OnPeripheralBarcodeScanned(object? sender, string raw)
    {
        var decoded = DecodeScannerInput(raw);
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return;
        }

        BarcodeScanned?.Invoke(this, decoded);
    }

    private void OnPeripheralStatusChanged(object? sender, EventArgs e) =>
        PeripheralStatusChanged?.Invoke(this, EventArgs.Empty);

    private async Task RunFaultToleranceLoopAsync(CancellationToken cancellationToken)
    {
        var intervalSeconds = Math.Clamp(_options.HealthProbeIntervalSeconds, 5, 300);
        _logger.LogInformation(
            "Hardware fault-tolerance loop started (probe every {Seconds}s).",
            intervalSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var health = await _peripherals.ProbeAsync(cancellationToken).ConfigureAwait(false);
                var printerOk = !_thermal.Enabled || health.IsPrinterConnected;
                var scannerOk = !_options.ScannerEnabled
                    || string.Equals(health.ScannerStatus, "Listening", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(health.ScannerStatus, "Disabled", StringComparison.OrdinalIgnoreCase);

                if (!printerOk || !scannerOk)
                {
                    _logger.LogWarning(
                        "Peripheral health degraded (printer={Printer}, scanner={Scanner}). Reconnecting.",
                        health.IsPrinterConnected,
                        health.ScannerStatus);
                    await _peripherals.ReconnectAllAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Hardware fault-tolerance probe failed; will retry.");
                try
                {
                    await _peripherals.ReconnectAllAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception reconnectEx) when (reconnectEx is not OperationCanceledException)
                {
                    _logger.LogDebug(reconnectEx, "Hardware reconnect attempt failed.");
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Hardware fault-tolerance loop stopped.");
    }

    private static ReceiptPrintRequest AnnotateStatutoryVat(ReceiptPrintRequest request)
    {
        IReadOnlyList<TaxBreakDownDto> taxes = request.TaxBreakdown;
        if (taxes.Count == 0 && request.LineItems.Count > 0)
        {
            var taxable = request.LineItems.Sum(i => i.Total - i.TotalVat);
            var vat = request.LineItems.Sum(i => i.TotalVat);
            taxes =
            [
                new TaxBreakDownDto
                {
                    RateId = "A",
                    TaxableAmount = taxable,
                    TaxAmount = vat
                }
            ];
        }

        return new ReceiptPrintRequest
        {
            TradingName = request.TradingName,
            SellerTin = request.SellerTin,
            AddressLines = request.AddressLines,
            ContactPhone = request.ContactPhone,
            ContactEmail = request.ContactEmail,
            BuyerTin = request.BuyerTin,
            BuyerName = request.BuyerName,
            InvoiceNumber = request.InvoiceNumber,
            InvoiceDateTime = request.InvoiceDateTime,
            LineItems = request.LineItems,
            TaxBreakdown = taxes,
            SubtotalNet = request.ResolveSubtotalNet(),
            TotalVat = request.ResolveTotalVat(),
            InvoiceTotal = request.InvoiceTotal,
            AmountTendered = request.AmountTendered,
            FiscalResponse = request.FiscalResponse
        };
    }
}

/// <summary>
/// Starts the hardware fault-tolerance reconnection loop for the life of the POS host process.
/// </summary>
public sealed class HardwareIntegrationWatchdogService : BackgroundService
{
    private readonly IHardwareIntegrationService _hardware;
    private readonly IOptions<HardwarePeripheralOptions> _options;
    private readonly ILogger<HardwareIntegrationWatchdogService> _logger;

    public HardwareIntegrationWatchdogService(
        IHardwareIntegrationService hardware,
        IOptions<HardwarePeripheralOptions> options,
        ILogger<HardwareIntegrationWatchdogService> logger)
    {
        _hardware = hardware;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("Hardware integration watchdog idle (peripherals disabled).");
            return;
        }

        try
        {
            await _hardware.StartFaultToleranceLoopAsync(stoppingToken).ConfigureAwait(false);
            if (_options.Value.ScannerEnabled)
            {
                await _hardware.StartScannerMonitoringAsync(stoppingToken).ConfigureAwait(false);
            }

            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown.
        }
        finally
        {
            await _hardware.StopFaultToleranceLoopAsync().ConfigureAwait(false);
            await _hardware.StopScannerMonitoringAsync().ConfigureAwait(false);
        }
    }
}
