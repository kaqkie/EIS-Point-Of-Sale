using PointOfSale.App.Services;
using PointOfSale.Mra.Contracts.Sales;
using Xunit;

namespace PointOfSale.Tests;

public sealed class FiscalConnectivityAndReceiptTests
{
    [Fact]
    public void ProbeTimeout_IsFlooredAt30Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ConnectionStatusService.ResolveProbeTimeout(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(30), ConnectionStatusService.ResolveProbeTimeout(TimeSpan.FromSeconds(8)));
        Assert.Equal(TimeSpan.FromSeconds(60), ConnectionStatusService.ResolveProbeTimeout(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Enricher_BuildsVerificationUrl_WhenMraOmitsIt()
    {
        var enriched = FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
            new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = "ART-1",
                FiscalSignature = "FSIG-LIVE-001"
            },
            "ART-1");

        Assert.Equal("FSIG-LIVE-001", enriched.ResolveFiscalSignature());
        Assert.False(string.IsNullOrWhiteSpace(enriched.VerificationUrl));
        Assert.Contains("dev-eis-portal.mra.mw/verify", enriched.VerificationUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("invoice=ART-1", enriched.VerificationUrl, StringComparison.Ordinal);
        Assert.Contains("sig=FSIG-LIVE-001", enriched.VerificationUrl, StringComparison.Ordinal);
        Assert.False(FiscalReceiptEnricher.IsOfflinePlaceholder(enriched.ResolveFiscalSignature()));
    }

    [Fact]
    public void Enricher_DoesNotFabricateQr_ForOfflinePlaceholder()
    {
        var enriched = FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
            new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = "ART-2",
                FiscalSignature = FiscalReceiptEnricher.OfflinePendingPlaceholder
            },
            "ART-2");

        Assert.True(FiscalReceiptEnricher.IsOfflinePlaceholder(enriched.ResolveFiscalSignature()));
        Assert.True(string.IsNullOrWhiteSpace(enriched.VerificationUrl));
    }

    [Fact]
    public void HasPrintableFiscalData_True_ForOnlineSignatureWithoutUrl()
    {
        Assert.True(QueueReceiptPrintHelper.HasPrintableFiscalData(
            new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = "ART-3",
                FiscalSignature = "FSIG-ABC"
            }));
    }

    [Fact]
    public void MraReceiptLayout_UsesExplicitVat175_AndQrWhenSynced()
    {
        var layout = new MraReceiptLayoutService().Build(
            new ReceiptPrintRequest
            {
                TradingName = "Albert Retail",
                SellerTin = "1234567890",
                AddressLines = ["City Center"],
                InvoiceNumber = "ART-LAYOUT-1",
                InvoiceDateTime = new DateTime(2026, 7, 24, 9, 0, 0),
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
                SubtotalNet = 100m,
                TotalVat = 17.5m,
                FiscalResponse = new SubmitSalesTransactionResponseData
                {
                    InvoiceNumber = "ART-LAYOUT-1",
                    FiscalSignature = "FSIG-LIVE-LAYOUT",
                    VerificationUrl = "https://dev-eis-portal.mra.mw/verify?invoice=ART-LAYOUT-1&sig=FSIG-LIVE-LAYOUT"
                }
            },
            charactersPerLine: 42);

        Assert.Contains("VAT 17.5%", string.Join('\n', layout.OrderedTextLines), StringComparison.Ordinal);
        Assert.Contains("VAT 17.5%", layout.LineItems[0].VatBreakdownLine, StringComparison.Ordinal);
        Assert.Contains(layout.TotalsLines, l => l.StartsWith("VAT 17.5%", StringComparison.Ordinal));
        Assert.False(layout.FiscalStatus.IsOfflinePending);
        Assert.True(layout.FiscalStatus.IncludeQrCode);
        Assert.NotNull(layout.FiscalStatus.QrModuleMatrix);
        Assert.NotNull(layout.FiscalStatus.QrCodeImage);
        Assert.Contains(layout.FiscalStatus.BodyLines, l => l.Contains("SYNCED", StringComparison.Ordinal));
    }

    [Fact]
    public void MraReceiptLayout_OfflinePending_OmitsQr()
    {
        var layout = new MraReceiptLayoutService().Build(
            new ReceiptPrintRequest
            {
                TradingName = "Albert Retail",
                SellerTin = "1234567890",
                AddressLines = ["City Center"],
                InvoiceNumber = "ART-LAYOUT-2",
                InvoiceDateTime = DateTime.UtcNow,
                LineItems =
                [
                    new InvoiceLineItemDto
                    {
                        Id = 1,
                        ProductCode = "SKU1",
                        Description = "Milk",
                        UnitPrice = 200m,
                        Quantity = 1m,
                        Total = 235m,
                        TotalVat = 35m,
                        TaxRateId = "A"
                    }
                ],
                TaxBreakdown =
                [
                    new TaxBreakDownDto { RateId = "A", TaxableAmount = 200m, TaxAmount = 35m }
                ],
                InvoiceTotal = 235m,
                AmountTendered = 235m,
                FiscalResponse = new SubmitSalesTransactionResponseData
                {
                    InvoiceNumber = "ART-LAYOUT-2",
                    FiscalSignature = FiscalReceiptEnricher.OfflinePendingPlaceholder
                }
            });

        Assert.True(layout.FiscalStatus.IsOfflinePending);
        Assert.False(layout.FiscalStatus.IncludeQrCode);
        Assert.Null(layout.FiscalStatus.QrCodeImage);
        Assert.Contains(layout.FiscalStatus.BodyLines, l => l.Contains("OFFLINE", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(layout.OrderedTextLines, l => l.Contains("VAT 17.5%", StringComparison.Ordinal));
    }
}
