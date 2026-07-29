using System.Text.Json;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Serialization;
using Xunit;

namespace PointOfSale.Tests;

public sealed class SubmittedTransactionDeserializationTests
{
    [Fact]
    public void Deserialize_LastSubmittedOnline_MapsSandboxEnvelope()
    {
        const string json = """
            {
              "statusCode": 1,
              "remark": "Success",
              "data": {
                "dateSubmitted": "2026-07-27T10:51:31.000Z",
                "invoiceHeader": {
                  "invoiceNumber": "E-De-JYxh-B",
                  "invoiceDateTime": "2026-07-27T10:51:31.000Z",
                  "sellerTIN": "20162939",
                  "siteId": "your-site-id",
                  "globalConfigVersion": 1,
                  "taxpayerConfigVersion": 1,
                  "terminalConfigVersion": 1,
                  "isReliefSupply": false,
                  "paymentMethod": "Cash"
                },
                "invoiceLineItems": [
                  {
                    "id": 1,
                    "productCode": "PROD-01",
                    "description": "Retail Item",
                    "unitPrice": 180.00,
                    "quantity": 1,
                    "discount": 0.00,
                    "total": 180.00,
                    "totalVAT": 26.15,
                    "taxRateId": "T",
                    "isProduct": true
                  }
                ],
                "invoiceSummary": {
                  "taxBreakDown": [
                    {
                      "rateId": "T",
                      "taxableAmount": 180.00,
                      "taxAmount": 26.15
                    }
                  ],
                  "totalVAT": 26.15,
                  "invoiceTotal": 180.00,
                  "amountTendered": 180.00
                }
              },
              "errors": null
            }
            """;

        var response = JsonSerializer.Deserialize<MraApiResponse<SubmittedTransactionData>>(
            json,
            MraJson.SerializerOptions);

        Assert.NotNull(response);
        Assert.True(response!.IsSuccess);
        Assert.Equal(1, response.StatusCode);
        Assert.NotNull(response.Data);
        Assert.Equal("E-De-JYxh-B", response.Data!.InvoiceHeader?.InvoiceNumber);
        Assert.Equal(1, response.Data.InvoiceHeader?.GlobalConfigVersion);
        Assert.Equal("T", response.Data.InvoiceLineItems?[0].TaxRateId);
        Assert.Equal(180.00m, response.Data.InvoiceLineItems?[0].UnitPrice);
        Assert.Equal(26.15m, response.Data.InvoiceSummary?.TotalVat);
        Assert.Equal("T", response.Data.InvoiceSummary?.TaxBreakDown?[0].RateId);
    }

    [Fact]
    public void LastSubmittedOfflineParser_MapsEnvelope_AndValidatesCompositeInvoice_E_De_JYxh_B()
    {
        const string json = SampleLastSubmittedJson;

        var parser = new PointOfSale.Mra.Services.LastSubmittedOfflineTransactionResponseService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PointOfSale.Mra.Services.LastSubmittedOfflineTransactionResponseService>.Instance);

        var parsed = parser.ParseJson(json);
        Assert.True(parsed.Success);
        Assert.Equal("E-De-JYxh-B", parsed.InvoiceNumber);
        Assert.True(parsed.HasCompositeInvoiceNumber);
        Assert.Equal("offline-sig-sample", parsed.Data!.InvoiceSummary!.OfflineSignature);
        Assert.Equal(26.15m, parsed.Data.InvoiceSummary.TotalVat);

        var sequence = parser.CheckSequenceContinuity(parsed.Data);
        Assert.True(sequence.IsValid);
        Assert.Equal("E-De-JYxh-B", sequence.InvoiceNumber);
        Assert.NotNull(sequence.TransactionCount);
        Assert.True(PointOfSale.Mra.Billing.MraInvoiceNumberGenerator.TryParseComposite(
            "E-De-JYxh-B",
            out var parts));
        Assert.Equal(parts.TaxpayerId, sequence.TaxpayerId);
    }

    [Fact]
    public void LastSubmittedOnlineParser_MapsDetailedInvoice_AndReconcilesAuditStatus()
    {
        var parser = new PointOfSale.Mra.Services.LastSubmittedOnlineTransactionResponseService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PointOfSale.Mra.Services.LastSubmittedOnlineTransactionResponseService>.Instance);

        var parsed = parser.ParseJson(SampleLastSubmittedJson);
        Assert.True(parsed.Success);
        Assert.Equal("E-De-JYxh-B", parsed.InvoiceNumber);
        Assert.Equal("20162939", parsed.Data!.InvoiceHeader!.SellerTin);
        Assert.Equal(1, parsed.Data.InvoiceHeader.GlobalConfigVersion);
        Assert.Equal("PROD-01", parsed.Data.InvoiceLineItems![0].ProductCode);
        Assert.Equal(180.00m, parsed.Data.InvoiceLineItems[0].UnitPrice);
        Assert.Equal(26.15m, parsed.Data.InvoiceLineItems[0].TotalVat);
        Assert.Equal("T", parsed.Data.InvoiceLineItems[0].TaxRateId);
        Assert.Equal("offline-sig-sample", parsed.Data.InvoiceSummary!.OfflineSignature);
        Assert.Equal(180.00m, parsed.Data.InvoiceSummary.InvoiceTotal);

        var reconciliation = parser.ReconcileOnlineSubmission(
            SampleLastSubmittedJson,
            expectedSellerTin: null,
            lastLocalOnlineInvoiceNumber: "E-De-JYxh-B");

        Assert.True(reconciliation.Success);
        Assert.True(reconciliation.SequenceValid);
        Assert.Equal(PointOfSale.Mra.Services.OnlineLocalRelation.Aligned, reconciliation.LocalRelation);
        Assert.Equal("E-De-JYxh-B", reconciliation.Audit!.InvoiceNumber);
        Assert.Equal(1, reconciliation.Audit.LineItemCount);
        Assert.Equal(26.15m, reconciliation.Audit.TotalVat);
        Assert.Contains("PROD-01", reconciliation.Audit.ProductCodes);
    }

    private const string SampleLastSubmittedJson = """
        {
          "statusCode": 1,
          "remark": "Success",
          "data": {
            "dateSubmitted": "2026-07-27T10:51:31.000Z",
            "invoiceHeader": {
              "invoiceNumber": "E-De-JYxh-B",
              "invoiceDateTime": "2026-07-27T10:51:31.000Z",
              "sellerTIN": "20162939",
              "buyerTIN": "10020030",
              "siteId": "your-site-id",
              "globalConfigVersion": 1,
              "taxpayerConfigVersion": 1,
              "terminalConfigVersion": 1,
              "isReliefSupply": false,
              "paymentMethod": "Cash"
            },
            "invoiceLineItems": [
              {
                "id": 1,
                "productCode": "PROD-01",
                "description": "Retail Item",
                "unitPrice": 180.00,
                "quantity": 1,
                "discount": 0.00,
                "total": 180.00,
                "totalVAT": 26.15,
                "taxRateId": "T",
                "isProduct": true
              }
            ],
            "invoiceSummary": {
              "taxBreakDown": [
                {
                  "rateId": "T",
                  "taxableAmount": 180.00,
                  "taxAmount": 26.15
                }
              ],
              "totalVAT": 26.15,
              "offlineSignature": "offline-sig-sample",
              "invoiceTotal": 180.00,
              "amountTendered": 180.00
            }
          },
          "errors": null
        }
        """;

    [Fact]
    public void CheckSequenceContinuity_RejectsTaxpayerMismatch()
    {
        var invoice = PointOfSale.Mra.Billing.MraInvoiceNumberGenerator.Generate(
            taxpayerId: 20162939,
            terminalPosition: 1,
            transactionDateUtc: new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc),
            transactionCount: 1);

        var data = new SubmittedTransactionData
        {
            InvoiceHeader = new SubmittedInvoiceHeader
            {
                InvoiceNumber = invoice,
                SellerTin = "20162939",
                SiteId = "SITE-01",
                PaymentMethod = "Cash"
            },
            InvoiceSummary = new SubmittedInvoiceSummary
            {
                TotalVat = 0m,
                InvoiceTotal = 0m,
                AmountTendered = 0m
            }
        };

        var parser = new PointOfSale.Mra.Services.LastSubmittedOfflineTransactionResponseService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PointOfSale.Mra.Services.LastSubmittedOfflineTransactionResponseService>.Instance);

        var check = parser.CheckSequenceContinuity(data, expectedSellerTin: "99999999");
        Assert.False(check.IsValid);
        Assert.Contains("sellerTIN", check.Message!, StringComparison.OrdinalIgnoreCase);
    }
}
