using System.Text.Json;
using PointOfSale.Mra.Contracts.Common;
using PointOfSale.Mra.Contracts.Stock;
using PointOfSale.Mra.Serialization;
using Xunit;

namespace PointOfSale.Tests;

public sealed class WarehouseInventoryDeserializationTests
{
    [Fact]
    public void Deserialize_AllowsNullPriceAndQuantity()
    {
        const string json = """
            {
              "statusCode": 1,
              "remark": "Success",
              "data": {
                "stocks": [
                  {
                    "barcode": "990663831995",
                    "productName": "Air Cleaner",
                    "productDescription": "Air Cleaner  13780-58B00",
                    "currentQuantity": 12,
                    "uom": "Each",
                    "price": null
                  },
                  {
                    "barcode": "456076017395",
                    "productName": "Filter",
                    "currentQuantity": null,
                    "uom": "Each",
                    "price": 20000
                  }
                ],
                "total": 2,
                "page": 1,
                "pageSize": 50
              },
              "errors": []
            }
            """;

        var response = JsonSerializer.Deserialize<EisApiResponse<PagedResponse<WarehouseInventoryItemDto>>>(
            json,
            MraJson.SerializerOptions);

        Assert.NotNull(response);
        Assert.True(response!.IsSuccess);
        Assert.Equal(2, response.Data!.GetItems().Count);
        Assert.Null(response.Data.GetItems()[0].Price);
        Assert.False(response.Data.GetItems()[0].HasUnitPrice);
        Assert.Equal(0m, response.Data.GetItems()[0].ResolveUnitPrice());
        Assert.Equal(12m, response.Data.GetItems()[0].ResolveQuantity());
        Assert.Equal(20000m, response.Data.GetItems()[1].ResolveUnitPrice());
        Assert.Equal(0m, response.Data.GetItems()[1].ResolveQuantity());
    }
}
