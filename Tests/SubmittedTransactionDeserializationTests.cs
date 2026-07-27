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
}
