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
    public void MraHttpClient_Timeout_IsFlooredAt30Seconds()
    {
        Assert.Equal(30, PointOfSale.Infrastructure.Http.MraHttpClientFactory.MinimumTimeoutSeconds);
        var shortOpts = new PointOfSale.Mra.Options.MraApiOptions { HttpTimeoutSeconds = 5 };
        Assert.Equal(TimeSpan.FromSeconds(30), PointOfSale.Infrastructure.Http.MraHttpClientFactory.ResolveTimeout(shortOpts));
        var longOpts = new PointOfSale.Mra.Options.MraApiOptions { HttpTimeoutSeconds = 90 };
        Assert.Equal(TimeSpan.FromSeconds(90), PointOfSale.Infrastructure.Http.MraHttpClientFactory.ResolveTimeout(longOpts));
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
                ContactPhone = "+265 1 234 567",
                ContactEmail = "shop@albertretail.mw",
                BuyerTin = "9876543210",
                BuyerName = "Test Buyer",
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

        var text = string.Join('\n', layout.OrderedTextLines);
        Assert.Contains(MraReceiptLayoutService.LegalReceiptStartBanner, text, StringComparison.Ordinal);
        Assert.Contains(MraReceiptLayoutService.LegalReceiptEndBanner, text, StringComparison.Ordinal);
        Assert.Contains(MraReceiptLayoutService.VatRegisteredBanner, text, StringComparison.Ordinal);
        Assert.Contains("MALAWI REVENUE AUTHORITY", text, StringComparison.Ordinal);
        Assert.Contains("RECEIPT NUMBER: ART-LAYOUT-1", text, StringComparison.Ordinal);
        Assert.Contains("Buyer's TIN: 9876543210", text, StringComparison.Ordinal);
        Assert.Contains("Date: 2026-07-24", text, StringComparison.Ordinal);
        Assert.Contains("Time: 09:00:00", text, StringComparison.Ordinal);
        Assert.Contains("TAXABLE A-17.5%", text, StringComparison.Ordinal);
        Assert.Contains("VAT A=17.5%", text, StringComparison.Ordinal);
        Assert.Contains("TOTAL VAT", text, StringComparison.Ordinal);
        Assert.Contains("AMOUNT", text, StringComparison.Ordinal);
        Assert.Contains("CHANGE", text, StringComparison.Ordinal);
        Assert.Contains(" A", layout.LineItems[0].QuantityPriceLine, StringComparison.Ordinal);
        Assert.False(layout.FiscalStatus.IsOfflinePending);
        Assert.True(layout.FiscalStatus.IncludeQrCode);
        Assert.NotNull(layout.FiscalStatus.QrModuleMatrix);
        Assert.NotNull(layout.FiscalStatus.QrCodeImage);
        // QR placeholder sits immediately above the END banner.
        var qrIndex = layout.OrderedTextLines.ToList().IndexOf(MraReceiptLayoutService.QrPlaceholderMarker);
        var endIndex = layout.OrderedTextLines.ToList().FindIndex(l => l.Contains("END OF LEGAL RECEIPT", StringComparison.Ordinal));
        Assert.True(qrIndex >= 0 && endIndex > qrIndex);
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

        var text = string.Join('\n', layout.OrderedTextLines);
        Assert.True(layout.FiscalStatus.IsOfflinePending);
        Assert.False(layout.FiscalStatus.IncludeQrCode);
        Assert.Null(layout.FiscalStatus.QrCodeImage);
        Assert.DoesNotContain(MraReceiptLayoutService.QrPlaceholderMarker, text, StringComparison.Ordinal);
        Assert.Contains(MraReceiptLayoutService.LegalReceiptStartBanner, text, StringComparison.Ordinal);
        Assert.Contains(MraReceiptLayoutService.LegalReceiptEndBanner, text, StringComparison.Ordinal);
        Assert.Contains("VAT A=17.5%", text, StringComparison.Ordinal);
        Assert.Contains("OFFLINE", text, StringComparison.OrdinalIgnoreCase);
    }
}
