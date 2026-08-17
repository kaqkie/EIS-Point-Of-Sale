using PointOfSale.App.Services;
using PointOfSale.Core.Pricing;
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
    public void HasPrintableFiscalData_False_ForSignatureWithoutValidationUrl()
    {
        // Signature-only must not count as printable — that skipped ValidationURL rebuild.
        Assert.False(QueueReceiptPrintHelper.HasPrintableFiscalData(
            new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = "ART-3",
                FiscalSignature = "FSIG-ABC"
            }));
    }

    [Fact]
    public void MraReceiptLayout_UsesExplicitVat175_WithVerificationQr()
    {
        var layout = new MraReceiptLayoutService().Build(
            new ReceiptPrintRequest
            {
                TradingName = "Albert Retail",
                SellerTin = "2007123456",
                AddressLines = ["City Center", "Lilongwe"],
                ContactPhone = "+265 1 234 567",
                ContactEmail = "shop@albertretail.mw",
                BuyerTin = "9876543210",
                BuyerName = "Test Buyer",
                InvoiceNumber = "CV-WEB-JY4+-C",
                InvoiceDateTime = new DateTime(2026, 7, 24, 9, 0, 0),
                PaymentMethod = "Cash",
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
                    InvoiceNumber = "CV-WEB-JY4+-C",
                    FiscalSignature = "FSIG-LIVE-LAYOUT",
                    VerificationUrl = "https://dev-eis-portal.mra.mw/verify?invoice=CV-WEB-JY4+-C&sig=FSIG-LIVE-LAYOUT"
                }
            },
            charactersPerLine: 42);

        var text = string.Join('\n', layout.OrderedTextLines);
        Assert.Contains(MraReceiptLayoutService.LegalReceiptStartBanner, text, StringComparison.Ordinal);
        Assert.Contains(MraReceiptLayoutService.LegalReceiptEndBanner, text, StringComparison.Ordinal);
        Assert.Contains(MraReceiptLayoutService.VatRegisteredBanner, text, StringComparison.Ordinal);
        Assert.Contains("MALAWI REVENUE AUTHORITY", text, StringComparison.Ordinal);
        Assert.Contains("Albert Retail", text, StringComparison.Ordinal);
        Assert.Contains("City Center", text, StringComparison.Ordinal);
        Assert.Contains("MOB: +265 1 234 567", text, StringComparison.Ordinal);
        Assert.Contains("Email: shop@albertretail.mw", text, StringComparison.Ordinal);
        Assert.Contains("Merchant TIN: 2007123456", text, StringComparison.Ordinal);
        Assert.Contains("FISCAL RECEIPT NUMBER: CV-WEB-JY4+-C", text, StringComparison.Ordinal);
        Assert.Contains("Buyer's TIN: 9876543210", text, StringComparison.Ordinal);
        Assert.Contains("Buyer's Name: Test Buyer", text, StringComparison.Ordinal);
        Assert.Contains("QTY  DESCRIPTION", text, StringComparison.Ordinal);
        Assert.Contains("AMOUNT", text, StringComparison.Ordinal);
        Assert.Contains("Bread", text, StringComparison.Ordinal);
        // Qty line uses VAT-inclusive shelf amount (Total + TotalVat = 135.00)
        Assert.Contains("1 X 135.00", text, StringComparison.Ordinal);
        Assert.Contains("135.00 A", text, StringComparison.Ordinal);
        Assert.Contains("TAXABLE A-17.5%", text, StringComparison.Ordinal);
        Assert.Contains("VAT A-17.5%", text, StringComparison.Ordinal);
        Assert.Contains("TOTAL VAT", text, StringComparison.Ordinal);
        Assert.Contains("GRAND TOTAL", text, StringComparison.Ordinal);
        Assert.Contains("PAYMENT METHOD: CASH", text, StringComparison.Ordinal);
        Assert.Contains("AMOUNT TENDERED", text, StringComparison.Ordinal);
        Assert.Contains("CHANGE", text, StringComparison.Ordinal);
        Assert.Contains("TRANSACTION DATE/TIME: 2026-07-24 09:00:00", text, StringComparison.Ordinal);
        Assert.Contains("Date/Time: 2026-07-24 09:00:00", text, StringComparison.Ordinal);
        Assert.False(layout.FiscalStatus.IsOfflinePending);
        Assert.True(layout.FiscalStatus.IncludeQrCode);
        Assert.NotNull(layout.FiscalStatus.QrModuleMatrix);
        Assert.NotNull(layout.FiscalStatus.QrCodeImage);
        Assert.Contains("Scan QR to verify with MRA", text, StringComparison.Ordinal);
        Assert.Contains(MraReceiptLayoutService.QrPlaceholderMarker, text, StringComparison.Ordinal);
        Assert.DoesNotContain("MRA EIS FISCAL SIGNATURE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Verification URL", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveInclusiveUnitPrice_MatchesEisShelfPriceExample()
    {
        var item = new InvoiceLineItemDto
        {
            Id = 1,
            ProductCode = "AIR",
            Description = "Air Cleaner SMA-230",
            UnitPrice = 17021.28m, // exclusive wire / cart net unit
            Quantity = 1m,
            Total = 17021.28m,
            TotalVat = 2978.72m,
            TaxRateId = "A"
        };

        Assert.Equal(20000.00m, MraReceiptLayoutService.ResolveInclusiveUnitPrice(item));
        Assert.Equal(20000.00m, MraReceiptLayoutService.ResolveInclusiveLineTotal(item));
        Assert.Equal("1 X 20,000.00", MraReceiptLayoutService.FormatQtyInclusiveUnitLine(1m, 20000m, 42));
    }

    [Fact]
    public void MraReceiptLayout_PrintsDiscountWhenApplied()
    {
        // EIS: 20,000.00 shelf − 1,000.00 discount ⇒ taxable 16,170.21 / VAT 2,829.79 / total 19,000.00
        var netAfter = PosTaxCalculator.ExtractExclusiveFromInclusive(
            19000m,
            PosTaxCalculator.MalawiStandardVatRatePercent);
        var vatAfter = PosTaxCalculator.RoundMoney(19000m - netAfter);
        var exclusiveDiscount = PosTaxCalculator.RoundMoney(
            PosTaxCalculator.ExtractExclusiveFromInclusive(
                20000m,
                PosTaxCalculator.MalawiStandardVatRatePercent) - netAfter);

        var layout = new MraReceiptLayoutService().Build(
            new ReceiptPrintRequest
            {
                TradingName = "Albert Retail",
                SellerTin = "2007123456",
                AddressLines = ["City Center"],
                InvoiceNumber = "ART-DISC-1",
                InvoiceDateTime = new DateTime(2026, 8, 11, 16, 0, 0),
                PaymentMethod = "Cash",
                LineItems =
                [
                    new InvoiceLineItemDto
                    {
                        Id = 1,
                        ProductCode = "990663831995",
                        Description = "Air Cleaner SMA-230",
                        UnitPrice = PosTaxCalculator.ExtractExclusiveUnitFromInclusive(
                            20000m,
                            PosTaxCalculator.MalawiStandardVatRatePercent),
                        Quantity = 1m,
                        Discount = exclusiveDiscount,
                        Total = netAfter,
                        TotalVat = vatAfter,
                        TaxRateId = "A"
                    }
                ],
                TaxBreakdown =
                [
                    new TaxBreakDownDto
                    {
                        RateId = "A",
                        TaxableAmount = netAfter,
                        TaxAmount = vatAfter
                    }
                ],
                InvoiceTotal = 19000m,
                AmountTendered = 20000m,
                SubtotalNet = netAfter,
                TotalVat = vatAfter
            },
            charactersPerLine: 42);

        var text = string.Join('\n', layout.OrderedTextLines);
        Assert.Equal(20000.00m, layout.LineItems[0].UnitPrice);
        Assert.Equal(1000.00m, layout.LineItems[0].LineDiscount);
        Assert.Equal(19000.00m, layout.LineItems[0].LineTotal);
        Assert.Contains("1 X 20,000.00", text, StringComparison.Ordinal);
        Assert.Contains("Air Cleaner SMA-230", text, StringComparison.Ordinal);
        Assert.Contains("DISCOUNT", text, StringComparison.Ordinal);
        Assert.Contains("-1,000.00", text, StringComparison.Ordinal);
        Assert.Contains("19,000.00 A", text, StringComparison.Ordinal);
        Assert.Contains("GRAND TOTAL", text, StringComparison.Ordinal);
        Assert.Contains("19,000.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MraReceiptLayout_PrintsInclusiveItemModeDiscount()
    {
        // Wire format after normalizer: inclusive unitPrice + inclusive discount.
        var layout = new MraReceiptLayoutService().Build(
            new ReceiptPrintRequest
            {
                TradingName = "Albert Retail",
                SellerTin = "2007123456",
                AddressLines = ["City Center"],
                InvoiceNumber = "ART-DISC-2",
                InvoiceDateTime = new DateTime(2026, 8, 12, 7, 0, 0),
                PaymentMethod = "Cash",
                LineItems =
                [
                    new InvoiceLineItemDto
                    {
                        Id = 1,
                        ProductCode = "747157490635",
                        Description = "Air Cleaner 13780-58JF02",
                        UnitPrice = 22000m,
                        Quantity = 1m,
                        Discount = 2000m,
                        Total = PosTaxCalculator.ExtractExclusiveFromInclusive(
                            20000m,
                            PosTaxCalculator.MalawiStandardVatRatePercent),
                        TotalVat = PosTaxCalculator.RoundMoney(
                            20000m - PosTaxCalculator.ExtractExclusiveFromInclusive(
                                20000m,
                                PosTaxCalculator.MalawiStandardVatRatePercent)),
                        TaxRateId = "A"
                    }
                ],
                TaxBreakdown =
                [
                    new TaxBreakDownDto
                    {
                        RateId = "A",
                        TaxableAmount = PosTaxCalculator.ExtractExclusiveFromInclusive(
                            20000m,
                            PosTaxCalculator.MalawiStandardVatRatePercent),
                        TaxAmount = PosTaxCalculator.RoundMoney(
                            20000m - PosTaxCalculator.ExtractExclusiveFromInclusive(
                                20000m,
                                PosTaxCalculator.MalawiStandardVatRatePercent))
                    }
                ],
                InvoiceTotal = 20000m,
                AmountTendered = 20000m,
                SubtotalNet = PosTaxCalculator.ExtractExclusiveFromInclusive(
                    20000m,
                    PosTaxCalculator.MalawiStandardVatRatePercent),
                TotalVat = PosTaxCalculator.RoundMoney(
                    20000m - PosTaxCalculator.ExtractExclusiveFromInclusive(
                        20000m,
                        PosTaxCalculator.MalawiStandardVatRatePercent))
            },
            charactersPerLine: 42);

        var text = string.Join('\n', layout.OrderedTextLines);
        Assert.Equal(22000.00m, layout.LineItems[0].UnitPrice);
        Assert.Equal(2000.00m, layout.LineItems[0].LineDiscount);
        Assert.Equal(20000.00m, layout.LineItems[0].LineTotal);
        Assert.Contains("1 X 22,000.00", text, StringComparison.Ordinal);
        Assert.Contains("DISCOUNT", text, StringComparison.Ordinal);
        Assert.Contains("-2,000.00", text, StringComparison.Ordinal);
        Assert.Contains("20,000.00 A", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatSellerTin_HandlesEmptyAndConfiguredValues()
    {
        Assert.Equal("NOT CONFIGURED", MraReceiptLayoutService.FormatSellerTin(" "));
        Assert.Equal("NOT CONFIGURED", MraReceiptLayoutService.FormatSellerTin("1234567890"));
        Assert.Equal("2007123456", MraReceiptLayoutService.FormatSellerTin("2007123456"));
    }

    [Fact]
    public void MraReceiptLayout_OfflinePendingPlaceholder_OmitsQr()
    {
        var layout = new MraReceiptLayoutService().Build(
            new ReceiptPrintRequest
            {
                TradingName = "Albert Retail",
                SellerTin = "2007123456",
                AddressLines = ["City Center"],
                InvoiceNumber = "ART-LAYOUT-2",
                InvoiceDateTime = DateTime.UtcNow,
                PaymentMethod = "Card",
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
        Assert.Contains("VAT A-17.5%", text, StringComparison.Ordinal);
        Assert.Contains("PAYMENT METHOD: CARD", text, StringComparison.Ordinal);
        Assert.Contains("OFFLINE", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MRA EIS FISCAL SIGNATURE", text, StringComparison.Ordinal);
        Assert.Contains("MOB: NOT CONFIGURED", text, StringComparison.Ordinal);
        Assert.Contains("Email: NOT CONFIGURED", text, StringComparison.Ordinal);
    }

    [Fact]
    public void MraReceiptLayout_PendingOfflineWithValidationUrl_IncludesQr()
    {
        const string offlineUrl =
            "https://dev-eis-portal.mra.mw/ReceiptValidation/Validate/?I=ABC&N=1&T=100&V=15&D=xyz&S=hmacSig";
        var layout = new MraReceiptLayoutService().Build(
            new ReceiptPrintRequest
            {
                TradingName = "Albert Retail",
                SellerTin = "2007123456",
                AddressLines = ["City Center"],
                InvoiceNumber = "ART-OFF-QR-1",
                InvoiceDateTime = DateTime.UtcNow,
                PaymentMethod = "Cash",
                LineItems =
                [
                    new InvoiceLineItemDto
                    {
                        Id = 1,
                        ProductCode = "SKU1",
                        Description = "Soap",
                        UnitPrice = 100m,
                        Quantity = 1m,
                        Total = 117.5m,
                        TotalVat = 17.5m,
                        TaxRateId = "A"
                    }
                ],
                TaxBreakdown =
                [
                    new TaxBreakDownDto { RateId = "A", TaxableAmount = 100m, TaxAmount = 17.5m }
                ],
                InvoiceTotal = 117.5m,
                AmountTendered = 120m,
                FiscalResponse = new SubmitSalesTransactionResponseData
                {
                    InvoiceNumber = "ART-OFF-QR-1",
                    FiscalSignature = "offline-hmac-signature",
                    ValidationUrl = offlineUrl,
                    VerificationUrl = offlineUrl
                }
            });

        var text = string.Join('\n', layout.OrderedTextLines);
        Assert.True(layout.FiscalStatus.IsOfflinePending);
        Assert.True(layout.FiscalStatus.IncludeQrCode);
        Assert.NotNull(layout.FiscalStatus.QrCodeImage);
        Assert.Equal(offlineUrl, layout.FiscalStatus.VerificationUrl);
        Assert.Contains("OFFLINE", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sync pending", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MraReceiptLayoutService.QrPlaceholderMarker, text, StringComparison.Ordinal);
        Assert.Contains("Offline ValidationURL QR", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiptPrintRequest_ResolvesFiscalNumberAndPaymentLabels()
    {
        var request = new ReceiptPrintRequest
        {
            TradingName = "Albert Retail",
            SellerTin = "2007123456",
            AddressLines = ["City Center"],
            InvoiceNumber = "LOCAL-1",
            InvoiceDateTime = DateTime.UtcNow,
            LineItems = [],
            TaxBreakdown = [],
            InvoiceTotal = 0m,
            AmountTendered = 0m,
            PaymentMethod = "MobileMoney",
            FiscalResponse = new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = "CV-WEB-JY4+-C"
            }
        };

        Assert.Equal("CV-WEB-JY4+-C", request.ResolveFiscalReceiptNumber());
        Assert.Equal("MOBILE MONEY", request.ResolvePaymentMethodLabel());
    }
}
