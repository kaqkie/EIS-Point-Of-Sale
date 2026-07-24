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
    public void LooksLikeValidationOrClientError_True_ForOpaqueSandboxInternalError()
    {
        var ex = new MraApiException(
            "MRA EIS HTTP 500: Internal Server Error — An internal error occurred",
            500,
            """{"message":"An internal error occurred"}""");

        Assert.True(MraApiException.IsOpaqueSandboxInternalError(ex.ResponseBody));
        Assert.True(ex.LooksLikeValidationOrClientError());
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
    }

    [Fact]
    public void MraTaxRateCodes_MapsLegacyT_ToStandardA()
    {
        Assert.Equal(MraTaxRateCodes.StandardVat, MraTaxRateCodes.Normalize("T"));
        Assert.Equal(MraTaxRateCodes.StandardVat, MraTaxRateCodes.Normalize(null));
        Assert.Equal("A", MraTaxRateCodes.Normalize("A"));
        Assert.Equal(17.5m, MraTaxRateCodes.ResolveRatePercent("A", [("A", 17.5m)]));
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
                SellerTin: "1234567890",
                SiteId: "City Center",
                GlobalConfigVersion: 1,
                TaxpayerConfigVersion: 1,
                TerminalConfigVersion: 1,
                StandardTaxRateId: "A",
                ConfiguredTaxRates: [("A", 17.5m)]));

        Assert.Equal("INV-1", normalized.InvoiceHeader.InvoiceNumber);
        Assert.Equal("1234567890", normalized.InvoiceHeader.SellerTin);
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
}
