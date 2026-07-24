using System.Text.Json;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Serialization;
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
                .AddTicks(7521), // sub-millisecond noise that must be truncated
            SellerTin = "1234567890",
            SiteId = "SITE-01",
            PaymentMethod = "Cash"
        };

        var json = JsonSerializer.Serialize(header, MraJson.SerializerOptions);

        Assert.Contains("\"invoiceDateTime\":\"2026-07-23T07:01:36.042Z\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("0427521", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeQueuedPayload_TrimsAndDropsEmptyOptionalFields()
    {
        var request = new SubmitSalesTransactionRequest
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = " INV-1 ",
                InvoiceDateTime = new DateTime(2026, 7, 23, 7, 1, 36, 42, DateTimeKind.Utc).AddTicks(7521),
                SellerTin = " 123 ",
                BuyerTin = "   ",
                BuyerName = "",
                SiteId = " SITE ",
                PaymentMethod = " Cash "
            },
            InvoiceLineItems =
            [
                new InvoiceLineItemDto
                {
                    Id = 99,
                    ProductCode = " P1 ",
                    Description = " Item ",
                    TaxRateId = " A ",
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
                    new TaxBreakDownDto { RateId = " A ", TaxableAmount = 100, TaxAmount = 17.5m }
                ],
                OfflineSignature = "  ",
                TotalVat = 17.5m,
                InvoiceTotal = 117.5m,
                AmountTendered = 120m
            }
        };

        var normalized = OfflineSalesQueueService.NormalizeQueuedPayloadForResubmit(request);

        Assert.Equal("INV-1", normalized.InvoiceHeader.InvoiceNumber);
        Assert.Equal("123", normalized.InvoiceHeader.SellerTin);
        Assert.Null(normalized.InvoiceHeader.BuyerTin);
        Assert.Null(normalized.InvoiceHeader.BuyerName);
        Assert.Equal("Cash", normalized.InvoiceHeader.PaymentMethod);
        Assert.Equal(1, normalized.InvoiceLineItems[0].Id);
        Assert.Equal("P1", normalized.InvoiceLineItems[0].ProductCode);
        Assert.Null(normalized.InvoiceSummary.OfflineSignature);
        Assert.Equal("A", normalized.InvoiceSummary.TaxBreakDown[0].RateId);
        Assert.Equal(0, normalized.InvoiceHeader.InvoiceDateTime.Ticks % TimeSpan.TicksPerMillisecond);
        Assert.Equal(DateTimeKind.Utc, normalized.InvoiceHeader.InvoiceDateTime.Kind);
    }
}
