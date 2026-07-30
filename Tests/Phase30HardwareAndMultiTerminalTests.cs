using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;
using Xunit;

namespace PointOfSale.Tests;

public sealed class Phase30HardwareAndMultiTerminalTests
{
    [Fact]
    public void EscPos_HighDensityQr_UsesLargerModulesAndEccH()
    {
        var qr = EscPosReceiptEncoder.BuildHighDensityQrCode("https://eis.mra.mw/verify/demo");
        Assert.True(qr.Length > 32);
        Assert.Contains((byte)0x1D, qr);
        // Module size 8
        Assert.Contains(qr, b => b == 0x08);
        // ECC H = 0x33
        Assert.Contains(qr, b => b == 0x33);
    }

    [Fact]
    public void EscPos_CashDrawerKick_IsEscPSequence()
    {
        var kick = EscPosReceiptEncoder.BuildCashDrawerKick();
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, kick);
    }

    [Fact]
    public void EscPos_AutoCut_IsGsV()
    {
        var cut = EscPosReceiptEncoder.BuildAutoCut();
        Assert.Equal(0x1D, cut[^3]);
        Assert.Equal(0x56, cut[^2]);
        Assert.Equal(0x00, cut[^1]);
    }

    [Fact]
    public void EscPos_HardwareTestPage_IncludesInitAndCut()
    {
        var page = EscPosReceiptEncoder.BuildHardwareTestPage(48, "https://eis.mra.mw/verify");
        Assert.Equal(0x1B, page[0]);
        Assert.Equal(0x40, page[1]);
        Assert.Equal(0x1D, page[^3]);
        Assert.Equal(0x56, page[^2]);
    }

    [Fact]
    public void RolePermissionCatalog_StoreManagerCanManageHardware()
    {
        var permissions = RolePermissionCatalog.GetPermissions(OperatorRoles.StoreManager);
        Assert.Contains(OperatorPermissions.ManageHardwarePeripherals, permissions);
        Assert.Contains(OperatorPermissions.OpenCashDrawer, permissions);
    }

    [Fact]
    public void MultiTerminalSyncResult_DisabledFactory_IsSuccessfulNoOp()
    {
        var result = MultiTerminalSyncResult.Disabled("off");
        Assert.False(result.Enabled);
        Assert.True(result.Success);
        Assert.Equal("off", result.Message);
    }

    [Fact]
    public async Task HardwarePeripheralService_Probe_WhenThermalDisabled_ReportsDisconnected()
    {
        var hardware = new HardwarePeripheralService(
            Options.Create(new HardwarePeripheralOptions { Enabled = true, ScannerEnabled = false }),
            Options.Create(new ThermalPrinterOptions { Enabled = false }),
            new FakeThermalPrinter(),
            new MraReceiptLayoutService(),
            NullLogger<HardwarePeripheralService>.Instance);

        var snapshot = await hardware.ProbeAsync();
        Assert.False(snapshot.IsPrinterConnected);
        Assert.Equal("Disabled", snapshot.ScannerStatus);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.LastError));
    }

    [Fact]
    public async Task HardwarePeripheralService_Reconnect_RetriesUntilSuccess()
    {
        var printer = new FlakyThermalPrinter(failTimes: 2);
        var hardware = new HardwarePeripheralService(
            Options.Create(new HardwarePeripheralOptions
            {
                Enabled = true,
                MaxReconnectAttempts = 3,
                ReconnectDelayMs = 10,
                CashDrawerEnabled = true
            }),
            Options.Create(new ThermalPrinterOptions { Enabled = true, PreferEscPos = true }),
            printer,
            new MraReceiptLayoutService(),
            NullLogger<HardwarePeripheralService>.Instance);

        await hardware.KickCashDrawerAsync();
        Assert.Equal(3, printer.Attempts);
        Assert.True(hardware.IsPrinterConnected);
    }

    private sealed class FakeThermalPrinter : IThermalPrinterHardwareService
    {
        public bool IsEnabled => false;
        public Task PrintReceiptAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task PrintRawAsync(byte[] payload, string documentName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public byte[] BuildEscPosPayload(ReceiptPrintRequest request) => [];
    }

    private sealed class FlakyThermalPrinter : IThermalPrinterHardwareService
    {
        private readonly int _failTimes;
        public int Attempts { get; private set; }

        public FlakyThermalPrinter(int failTimes) => _failTimes = failTimes;

        public bool IsEnabled => true;

        public Task PrintReceiptAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default) =>
            PrintRawAsync([], "r", cancellationToken);

        public Task PrintRawAsync(byte[] payload, string documentName, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (Attempts <= _failTimes)
            {
                throw new IOException("Simulated peripheral disconnect");
            }

            return Task.CompletedTask;
        }

        public byte[] BuildEscPosPayload(ReceiptPrintRequest request) => [];
    }
}
