using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Core.Security;
using Xunit;

namespace PointOfSale.Tests;

public sealed class FinancialClosureTests
{
    [Fact]
    public void EscPosZReportEncoder_IncludesVatAndGrossTotals()
    {
        var report = new ZReportBundle
        {
            ShiftId = 12,
            CashierName = "Alice",
            OpenedAtUtc = DateTime.UtcNow.Date.AddHours(7),
            ClosedAtUtc = DateTime.UtcNow.Date.AddHours(18),
            OpeningFloat = 5000m,
            CashSales = 10000m,
            CardSales = 2500m,
            MobileMoneySales = 1500m,
            OtherSales = 0m,
            GrossSales = 14000m,
            TotalVat = PosTaxCalculator.CalculateVatAmount(14000m / 1.165m, PosTaxCalculator.MalawiStandardVatRatePercent),
            ExpectedCashInDrawer = 15000m,
            ClosingCashCounted = 14950m,
            CashVariance = -50m,
            InvoiceCount = 8
        };

        var context = new ZReportPrintContext
        {
            TradingName = "Albert Retail Terminal",
            BranchId = "BLTY-01",
            SiteId = "SITE-1",
            ManagerSignOff = "Store Manager",
            BusinessDate = DateTime.Today,
            CumulativeGrossSalesMwk = 14000m,
            CumulativeVatMwk = report.TotalVat,
            TotalVoidsMwk = 100m,
            VoidCount = 1,
            AuditPassed = true,
            AuditMessage = "Fiscal audit OK"
        };

        var text = EscPosZReportEncoder.FormatPlainText(report, context, charactersPerLine: 48);
        Assert.Contains("Z-REPORT", text);
        Assert.Contains("Gross sales", text);
        Assert.Contains("Total VAT", text);
        Assert.Contains("CUMULATIVE FISCAL", text);
        Assert.Contains("Alice", text);

        var bytes = EscPosZReportEncoder.Encode(report, context, charactersPerLine: 48);
        Assert.True(bytes.Length > 64);
        Assert.Equal(0x1B, bytes[0]);
        Assert.Equal(0x40, bytes[1]);
    }

    [Fact]
    public void RolePermissionCatalog_StoreManagerCanCloseFinancialDay()
    {
        var permissions = RolePermissionCatalog.GetPermissions(OperatorRoles.StoreManager);
        Assert.Contains(OperatorPermissions.CloseFinancialDay, permissions);
        Assert.DoesNotContain(
            OperatorPermissions.CloseFinancialDay,
            RolePermissionCatalog.GetPermissions(OperatorRoles.Cashier));
    }

    [Fact]
    public void FinancialClosureOptions_DefaultsEnforceAuditGates()
    {
        var options = new FinancialClosureOptions();
        Assert.True(options.RequireQueueDrained);
        Assert.True(options.RequireFiscalSignatures);
        Assert.False(options.AllowCloseWithOpenShift);
        Assert.Equal(0.01m, options.VatBalanceToleranceMwk);
    }

    [Fact]
    public void ZReportPrintingService_BuildsEscPosWithoutHardware()
    {
        var service = new ZReportPrintingService(
            new FakeThermalPrinter(),
            Options.Create(new ThermalPrinterOptions
            {
                Enabled = true,
                PreferEscPos = true,
                CharactersPerLine = 42
            }),
            Options.Create(new TerminalDeploymentOptions
            {
                FallbackTradingName = "Albert Retail Terminal",
                BranchId = "BR-1",
                SiteId = "S-1"
            }),
            NullLogger<ZReportPrintingService>.Instance);

        var report = new ZReportBundle
        {
            ShiftId = 1,
            CashierName = "Bob",
            OpenedAtUtc = DateTime.UtcNow.AddHours(-4),
            GrossSales = 1000m,
            TotalVat = 148.94m,
            InvoiceCount = 2
        };

        var payload = service.BuildEscPosPayload(report);
        Assert.NotEmpty(payload);
        Assert.Contains("Bob", service.FormatPlainText(report));
    }

    [Fact]
    public void EndOfDaySummary_TracksClosureFlags()
    {
        var summary = new EndOfDaySummary
        {
            BusinessDate = DateTime.Today,
            TotalGrossSalesMwk = 5000m,
            TotalVatCollectedMwk = 744.68m,
            IsDayAlreadyClosed = false,
            HasOpenShift = true,
            AuditPassed = false,
            SummaryText = "preview"
        };

        Assert.True(summary.HasOpenShift);
        Assert.False(summary.IsDayAlreadyClosed);
        Assert.False(summary.AuditPassed);
    }

    private sealed class FakeThermalPrinter : IThermalPrinterHardwareService
    {
        public bool IsEnabled => true;

        public Task PrintReceiptAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PrintRawAsync(byte[] payload, string documentName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public byte[] BuildEscPosPayload(ReceiptPrintRequest request) => [0x1B, 0x40];
    }
}
