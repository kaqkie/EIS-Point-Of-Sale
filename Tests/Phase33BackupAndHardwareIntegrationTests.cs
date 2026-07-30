using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Pricing;
using PointOfSale.Mra.Contracts.Sales;
using Xunit;

namespace PointOfSale.Tests;

public sealed class Phase33BackupAndHardwareIntegrationTests
{
    [Fact]
    public void DatabaseBackupTriggers_IncludesEndOfDay()
    {
        Assert.Equal("EndOfDay", DatabaseBackupTriggers.EndOfDay);
    }

    [Fact]
    public void DatabaseBackupOptions_Phase33Defaults_AreProductionSafe()
    {
        var options = new DatabaseBackupOptions();
        Assert.True(options.BackupOnEndOfDay);
        Assert.Equal(21, options.EndOfDayHourLocal);
        Assert.Equal(30, options.EndOfDayWindowMinutes);
        Assert.Equal(30, options.RetentionDays);
        Assert.True(options.VerifyAfterBackup);
    }

    [Fact]
    public void DatabaseBackupStatusSnapshot_HoldsPhase33Fields()
    {
        var snapshot = new DatabaseBackupStatusSnapshot
        {
            IsBackupInProgress = false,
            LastBackupTimestamp = DateTime.UtcNow,
            BackupFileLocation = @"C:\ProgramData\AlbertRetailTerminal\Backups\demo.bak",
            BackupDirectory = @"C:\ProgramData\AlbertRetailTerminal\Backups",
            StorageUsageMb = 12.5,
            LastError = null,
            HistoryCount = 3
        };

        Assert.False(snapshot.IsBackupInProgress);
        Assert.Equal(12.5, snapshot.StorageUsageMb);
        Assert.Contains("demo.bak", snapshot.BackupFileLocation);
    }

    [Fact]
    public void HardwareIntegration_DecodeScanner_StripsAimAndGs1()
    {
        var service = CreateIntegration();
        Assert.Equal("6001234567890", service.DecodeScannerInput("]E06001234567890\r"));
        Assert.Equal("01234567890123", service.DecodeScannerInput("0101234567890123"));
        Assert.Equal(string.Empty, service.DecodeScannerInput("   "));
    }

    [Fact]
    public void HardwareIntegration_CashDrawerKick_IsEscP()
    {
        var kick = CreateIntegration().BuildCashDrawerKickCommand(0);
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }, kick);
    }

    [Fact]
    public void HardwareIntegration_EncodeStatutoryVatReceipt_IncludesVatBannerAndHighDensityQr()
    {
        var request = new ReceiptPrintRequest
        {
            TradingName = "Albert Retail",
            SellerTin = "12345678",
            AddressLines = ["Lilongwe"],
            InvoiceNumber = "INV-1",
            InvoiceDateTime = DateTime.UtcNow,
            LineItems =
            [
                new InvoiceLineItemDto
                {
                    Id = 1,
                    ProductCode = "SKU1",
                    Description = "Bread",
                    UnitPrice = 100m,
                    Quantity = 1m,
                    Total = 117.5m,
                    TotalVat = 17.5m,
                    TaxRateId = "A"
                }
            ],
            TaxBreakdown =
            [
                new TaxBreakDownDto
                {
                    RateId = "A",
                    TaxableAmount = 100m,
                    TaxAmount = 17.5m
                }
            ],
            InvoiceTotal = 117.5m,
            AmountTendered = 120m,
            FiscalResponse = new SubmitSalesTransactionResponseData
            {
                VerificationUrl = "https://eis.mra.mw/verify/demo"
            }
        };

        var bytes = CreateIntegration().EncodeStatutoryVatReceipt(request, 42);
        var ascii = System.Text.Encoding.ASCII.GetString(bytes);
        Assert.Contains($"VAT {PosTaxCalculator.MalawiStandardVatRatePercent:0.0}%", ascii);
        Assert.Contains((byte)0x08, bytes);
        Assert.Contains((byte)0x33, bytes);
    }

    private static HardwareIntegrationService CreateIntegration()
    {
        var peripherals = new HardwarePeripheralService(
            Options.Create(new HardwarePeripheralOptions { Enabled = true, ScannerEnabled = false }),
            Options.Create(new ThermalPrinterOptions { Enabled = false }),
            new FakeThermalPrinter(),
            new MraReceiptLayoutService(),
            NullLogger<HardwarePeripheralService>.Instance);

        return new HardwareIntegrationService(
            peripherals,
            new MraReceiptLayoutService(),
            Options.Create(new HardwarePeripheralOptions { Enabled = true, ScannerEnabled = false }),
            Options.Create(new ThermalPrinterOptions { Enabled = false }),
            NullLogger<HardwareIntegrationService>.Instance);
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
}
