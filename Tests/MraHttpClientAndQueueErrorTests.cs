using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Http;
using Xunit;

namespace PointOfSale.Tests;

public sealed class MraHttpClientAndQueueErrorTests
{
    [Fact]
    public void FormatHttpError_IncludesValidationDetailsFromBody()
    {
        var body = """
            {"statusCode":0,"remark":"Validation failed","errors":[{"errorCode":40001,"fieldName":"invoiceHeader.sellerTIN","errorMessage":"TIN is invalid"}]}
            """;

        var message = MraApiException.FormatHttpError(500, "Internal Server Error", body);

        Assert.Contains("MRA EIS HTTP 500", message, StringComparison.Ordinal);
        Assert.Contains("Validation failed", message, StringComparison.Ordinal);
        Assert.Contains("invoiceHeader.sellerTIN", message, StringComparison.Ordinal);
        Assert.Contains("TIN is invalid", message, StringComparison.Ordinal);
    }

    [Fact]
    public void LooksLikeValidationOrClientError_True_ForSandbox500WithErrors()
    {
        var ex = new MraApiException(
            "MRA EIS HTTP 500",
            500,
            """{"statusCode":0,"remark":"Invalid payload","errors":[{"errorCode":40001,"fieldName":"paymentMethod","errorMessage":"required"}]}""");

        Assert.True(ex.LooksLikeValidationOrClientError());
    }

    [Fact]
    public void LooksLikeValidationOrClientError_False_ForOpaqueSandboxInternalError()
    {
        var ex = new MraApiException(
            "MRA EIS HTTP 500: Internal Server Error — An internal error occurred",
            500,
            """{"message":"An internal error occurred"}""");

        Assert.True(MraApiException.IsOpaqueSandboxInternalError(ex.ResponseBody));
        // Opaque sandbox 500s are transient EIS failures — queue should retry, not quarantine.
        Assert.False(ex.LooksLikeValidationOrClientError());
    }

    [Fact]
    public void LooksLikeValidationOrClientError_True_ForHttpClientLifetimeFault()
    {
        var inner = new InvalidOperationException(
            "This instance has already started one or more requests. Properties can only be modified before sending the first request.");
        var ex = new MraApiException(
            "MRA EIS request failed: InvalidOperationException: " + inner.Message,
            httpStatusCode: 0,
            responseBody: null,
            inner: inner);

        Assert.True(ex.IsHttpClientLifetimeError());
        Assert.True(ex.LooksLikeValidationOrClientError());
    }

    [Fact]
    public void LooksLikeValidationOrClientError_False_ForEmptyInfrastructure500()
    {
        var ex = new MraApiException("MRA EIS HTTP 500: Internal Server Error", 500, "<html>Gateway error</html>");
        Assert.False(ex.LooksLikeValidationOrClientError());
    }

    [Fact]
    public void MraJson_WritesInvoiceDateTime_WithMillisecondPrecisionAndZ()
    {
        var header = new InvoiceHeaderDto
        {
            InvoiceNumber = "ART-1",
            InvoiceDateTime = new DateTime(2026, 7, 23, 7, 1, 36, 42, DateTimeKind.Utc)
                .AddTicks(7521),
            SellerTin = "1234567890",
            SiteId = "SITE-01",
            PaymentMethod = "Cash"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(header, PointOfSale.Mra.Serialization.MraJson.SerializerOptions);

        Assert.Contains("\"invoiceDateTime\":\"2026-07-23T07:01:36.042Z\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0427521", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeSiteId_ConvertsDisplayNamesToSiteCodes()
    {
        Assert.Equal("SITE-CITY-CENTER", MraFiscalPayloadNormalizer.NormalizeSiteId("City Center"));
        Assert.Equal("SITE-01", MraFiscalPayloadNormalizer.NormalizeSiteId("SITE-01"));
        Assert.Equal("SITE001", MraFiscalPayloadNormalizer.NormalizeSiteId("SITE001"));
        Assert.Equal(
            "BL7a9fe868-d512-4198-8b08-497e8f0fc10a",
            MraFiscalPayloadNormalizer.NormalizeSiteId("BL7a9fe868-d512-4198-8b08-497e8f0fc10a"));
        Assert.Equal(
            "7a9fe868-d512-4198-8b08-497e8f0fc10a",
            MraFiscalPayloadNormalizer.NormalizeSiteId("7a9fe868-d512-4198-8b08-497e8f0fc10a"));
    }

    [Fact]
    public void MraTaxRateCodes_MapsLegacyAliasesToOfficialRateId_A()
    {
        Assert.Equal(MraTaxRateCodes.StandardVat, MraTaxRateCodes.Normalize("T"));
        Assert.Equal(MraTaxRateCodes.StandardVat, MraTaxRateCodes.Normalize(null));
        Assert.Equal(MraTaxRateCodes.StandardVat, MraTaxRateCodes.Normalize("A"));
        Assert.Equal("A", MraTaxRateCodes.Normalize("STANDARD_17_5"));
        Assert.Equal(17.5m, MraTaxRateCodes.ResolveRatePercent("STANDARD_17_5", [("STANDARD_17_5", 17.5m)]));
        Assert.Equal(17.5m, MraTaxRateCodes.ResolveRatePercent("A", [("A", 17.5m)]));
        Assert.Equal(16.5m, MraTaxRateCodes.ResolveRatePercent("A", [("A", 16.5m)]));
        Assert.Equal("A", MraTaxRateCodes.ResolveStandardRateId(null, null));
        Assert.Equal("A", MraTaxRateCodes.ResolveStandardRateId([("A", 17.5m)], null));
        Assert.Equal("A", MraTaxRateCodes.ResolveStandardRateId(
            [("STANDARD_17_5", 17.5m), ("A", 17.5m)],
            ["A", "E"]));
    }

    [Fact]
    public void NormalizeQueuedPayload_AlignsFiscalFieldsForMra()
    {
        var request = new SubmitSalesTransactionRequest
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = " INV-1 ",
                InvoiceDateTime = new DateTime(2026, 7, 23, 7, 1, 36, 42, DateTimeKind.Utc).AddTicks(7521),
                SellerTin = " 1234567890 ",
                BuyerTin = "   ",
                BuyerName = "",
                SiteId = " City Center ",
                GlobalConfigVersion = 0,
                TaxpayerConfigVersion = 0,
                TerminalConfigVersion = 0,
                PaymentMethod = " Cash "
            },
            InvoiceLineItems =
            [
                new InvoiceLineItemDto
                {
                    Id = 99,
                    ProductCode = " P1 ",
                    Description = " Item ",
                    TaxRateId = " T ",
                    Quantity = 1,
                    UnitPrice = 100,
                    Total = 100,
                    TotalVat = 17.5m
                }
            ],
            InvoiceSummary = new InvoiceSummaryDto
            {
                TaxBreakDown =
                [
                    new TaxBreakDownDto { RateId = " T ", TaxableAmount = 100, TaxAmount = 17.5m }
                ],
                OfflineSignature = "  ",
                TotalVat = 17.5m,
                InvoiceTotal = 117.5m,
                AmountTendered = 120m
            }
        };

        var normalized = OfflineSalesQueueService.NormalizeQueuedPayloadForResubmit(
            request,
            new MraFiscalIdentityOverlay(
                SellerTin: "20162939",
                SiteId: "City Center",
                GlobalConfigVersion: 1,
                TaxpayerConfigVersion: 1,
                TerminalConfigVersion: 1,
                StandardTaxRateId: "A",
                ConfiguredTaxRates: [("A", 17.5m)]));

        Assert.Equal("INV-1", normalized.InvoiceHeader.InvoiceNumber);
        Assert.Equal("20162939", normalized.InvoiceHeader.SellerTin);
        Assert.Null(normalized.InvoiceHeader.BuyerTin);
        Assert.Null(normalized.InvoiceHeader.BuyerName);
        Assert.Equal("Cash", normalized.InvoiceHeader.PaymentMethod);
        Assert.Equal("SITE-CITY-CENTER", normalized.InvoiceHeader.SiteId);
        Assert.Equal(1, normalized.InvoiceHeader.GlobalConfigVersion);
        Assert.Equal(1, normalized.InvoiceLineItems[0].Id);
        Assert.Equal("P1", normalized.InvoiceLineItems[0].ProductCode);
        Assert.Equal("A", normalized.InvoiceLineItems[0].TaxRateId);
        Assert.Equal("A", normalized.InvoiceSummary.TaxBreakDown[0].RateId);
        Assert.Null(normalized.InvoiceSummary.OfflineSignature);
        Assert.Equal(0, normalized.InvoiceHeader.InvoiceDateTime.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(DateTimeKind.Utc, normalized.InvoiceHeader.InvoiceDateTime.Kind);
    }

    [Fact]
    public void NormalizeQueuedPayload_PreservesActivatedRateId_A_WithoutOverlay()
    {
        var request = new SubmitSalesTransactionRequest
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = "INV-3",
                InvoiceDateTime = DateTime.UtcNow,
                SellerTin = "20162939",
                SiteId = "SITE-01",
                PaymentMethod = "Cash",
                GlobalConfigVersion = 1,
                TaxpayerConfigVersion = 1,
                TerminalConfigVersion = 1
            },
            InvoiceLineItems =
            [
                new InvoiceLineItemDto
                {
                    Id = 1,
                    ProductCode = "P1",
                    Description = "Item",
                    TaxRateId = "A",
                    Quantity = 1,
                    UnitPrice = 100,
                    Total = 100,
                    TotalVat = 17.5m
                }
            ],
            InvoiceSummary = new InvoiceSummaryDto
            {
                TaxBreakDown =
                [
                    new TaxBreakDownDto { RateId = "A", TaxableAmount = 100, TaxAmount = 17.5m }
                ],
                TotalVat = 17.5m,
                InvoiceTotal = 117.5m,
                AmountTendered = 117.5m
            }
        };

        // Final wire hop (SalesTransactionService) normalizes without overlay —
        // must not invent STANDARD_17_5 over MRA's activated "A".
        var normalized = OfflineSalesQueueService.NormalizeQueuedPayloadForResubmit(request);
        Assert.Equal("A", normalized.InvoiceLineItems[0].TaxRateId);
        Assert.Equal("A", normalized.InvoiceSummary.TaxBreakDown[0].RateId);
    }

    [Fact]
    public void NormalizeQueuedPayload_DefaultsAmountTenderedToInvoiceTotal()
    {
        var request = new SubmitSalesTransactionRequest
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = "INV-2",
                InvoiceDateTime = DateTime.UtcNow,
                SellerTin = "1234567890",
                SiteId = "SITE-01",
                PaymentMethod = "Cash"
            },
            InvoiceLineItems =
            [
                new InvoiceLineItemDto
                {
                    Id = 1,
                    ProductCode = "P1",
                    Description = "Item",
                    TaxRateId = "A",
                    Quantity = 1,
                    UnitPrice = 100,
                    Total = 100,
                    TotalVat = 17.5m
                }
            ],
            InvoiceSummary = new InvoiceSummaryDto
            {
                TaxBreakDown =
                [
                    new TaxBreakDownDto { RateId = "A", TaxableAmount = 100, TaxAmount = 17.5m }
                ],
                TotalVat = 17.5m,
                InvoiceTotal = 0m,
                AmountTendered = 0m
            }
        };

        var normalized = OfflineSalesQueueService.NormalizeQueuedPayloadForResubmit(request);

        Assert.Equal(117.5m, normalized.InvoiceSummary.InvoiceTotal);
        Assert.Equal(117.5m, normalized.InvoiceSummary.AmountTendered);

        var json = System.Text.Json.JsonSerializer.Serialize(
            normalized,
            PointOfSale.Mra.Serialization.MraJson.SerializerOptions);
        Assert.Contains("\"amountTendered\":117.5", json.Replace(" ", string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeQueuedPayload_RecalculatesNetAndVatFromUnitFields()
    {
        var request = new SubmitSalesTransactionRequest
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = "INV-4",
                InvoiceDateTime = DateTime.UtcNow,
                SellerTin = "20162939",
                SiteId = "SITE-01",
                PaymentMethod = "Cash",
                GlobalConfigVersion = 1,
                TaxpayerConfigVersion = 1,
                TerminalConfigVersion = 2
            },
            InvoiceLineItems =
            [
                new InvoiceLineItemDto
                {
                    Id = 1,
                    ProductCode = "P1",
                    Description = "Item",
                    TaxRateId = "A",
                    Quantity = 2,
                    UnitPrice = 100,
                    Discount = 10,
                    Total = 999,
                    TotalVat = 0
                }
            ],
            InvoiceSummary = new InvoiceSummaryDto
            {
                TaxBreakDown =
                [
                    new TaxBreakDownDto { RateId = "A", TaxableAmount = 999, TaxAmount = 0 }
                ],
                TotalVat = 0,
                InvoiceTotal = 0,
                AmountTendered = 0
            }
        };

        var normalized = OfflineSalesQueueService.NormalizeQueuedPayloadForResubmit(
            request,
            new MraFiscalIdentityOverlay(
                StandardTaxRateId: "A",
                ConfiguredTaxRates: [("A", 17.5m)]));

        Assert.Equal(190m, normalized.InvoiceLineItems[0].Total);
        Assert.Equal(33.25m, normalized.InvoiceLineItems[0].TotalVat);
        Assert.Equal("A", normalized.InvoiceLineItems[0].TaxRateId);
        Assert.Equal("A", normalized.InvoiceSummary.TaxBreakDown[0].RateId);
        Assert.Equal(190m, normalized.InvoiceSummary.TaxBreakDown[0].TaxableAmount);
        Assert.Equal(33.25m, normalized.InvoiceSummary.TotalVat);
        Assert.Equal(223.25m, normalized.InvoiceSummary.InvoiceTotal);
        Assert.Equal(223.25m, normalized.InvoiceSummary.AmountTendered);
    }

    [Fact]
    public void NormalizeQueuedPayload_PreservesConfiguredVat_WhenOverlayRatesMissing()
    {
        // EIS sample uses A @ 16.5%. Without live rates, do not overwrite with statutory fallback.
        var request = new SubmitSalesTransactionRequest
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = "BMwna-B-JY5D-D",
                InvoiceDateTime = DateTime.UtcNow,
                SellerTin = "20122074",
                SiteId = "SITE-LUCHENZA",
                PaymentMethod = "Cash",
                GlobalConfigVersion = 1,
                TaxpayerConfigVersion = 1,
                TerminalConfigVersion = 1
            },
            InvoiceLineItems =
            [
                new InvoiceLineItemDto
                {
                    Id = 1,
                    ProductCode = "ART-OIL-1L",
                    Description = "Cooking Oil 1L",
                    TaxRateId = "A",
                    Quantity = 1,
                    UnitPrice = 5200m,
                    Discount = 0,
                    Total = 5200m,
                    TotalVat = 858m
                }
            ],
            InvoiceSummary = new InvoiceSummaryDto
            {
                TaxBreakDown =
                [
                    new TaxBreakDownDto { RateId = "A", TaxableAmount = 5200m, TaxAmount = 858m }
                ],
                TotalVat = 858m,
                InvoiceTotal = 6058m,
                AmountTendered = 6058m
            }
        };

        var normalized = OfflineSalesQueueService.NormalizeQueuedPayloadForResubmit(
            request,
            new MraFiscalIdentityOverlay(
                SellerTin: "20122074",
                SiteId: "Luchenza",
                StandardTaxRateId: "A"));

        Assert.Equal(858m, normalized.InvoiceLineItems[0].TotalVat);
        Assert.Equal(6058m, normalized.InvoiceSummary.InvoiceTotal);
        Assert.Equal(6058m, normalized.InvoiceSummary.AmountTendered);
        Assert.Equal("SITE-LUCHENZA", normalized.InvoiceHeader.SiteId);
    }
}
