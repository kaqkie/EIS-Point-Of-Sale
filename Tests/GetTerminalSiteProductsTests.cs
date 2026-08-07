using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;
using PointOfSale.Tests.Mocks;
using PointOfSale.Tests.Support;
using Xunit;

namespace PointOfSale.Tests;

public sealed class GetTerminalSiteProductsTests
{
    [Fact]
    public void Deserialize_TerminalSiteProducts_MapsCatalogFields()
    {
        const string json = """
            {
              "statusCode": 1,
              "remark": "Success",
              "data": [
                {
                  "productCode": "1234567890123",
                  "productName": "Coca Cola 500ml",
                  "description": "Carbonated soft drink",
                  "quantity": 120,
                  "unitOfMeasure": "Bottle",
                  "price": 1.5,
                  "siteId": "SITE001",
                  "productExpiryDate": "2025-12-31T00:00:00.000Z",
                  "minimumStockLevel": 10,
                  "taxRateId": "A",
                  "isProduct": true
                },
                {
                  "productCode": "SRV001",
                  "productName": "Car Wash Service",
                  "description": "Standard car wash",
                  "quantity": 0,
                  "unitOfMeasure": "Service",
                  "price": 10,
                  "siteId": "SITE001",
                  "productExpiryDate": null,
                  "minimumStockLevel": 0,
                  "taxRateId": "E",
                  "isProduct": false
                }
              ],
              "errors": []
            }
            """;

        var response = JsonSerializer.Deserialize<GetTerminalSiteProductsResponse>(
            json,
            MraJson.SerializerOptions);

        Assert.NotNull(response);
        Assert.True(response!.IsSuccess);
        Assert.Equal(2, response.Data!.Count);
        Assert.Equal("1234567890123", response.Data[0].ProductCode);
        Assert.Equal(120m, response.Data[0].Quantity);
        Assert.Equal("A", response.Data[0].TaxRateId);
        Assert.False(response.Data[1].IsProduct);
        Assert.Null(response.Data[1].ProductExpiryDate);
    }

    [Fact]
    public void Deserialize_TerminalSiteProducts_AllowsNullMinimumStockLevel()
    {
        const string json = """
            {
              "statusCode": 1,
              "remark": "Success",
              "data": [
                {
                  "productCode": "NULL-MIN",
                  "productName": "No reorder level",
                  "description": "EIS sometimes omits minimumStockLevel",
                  "quantity": 3,
                  "unitOfMeasure": "Each",
                  "price": 2.5,
                  "siteId": "SITE001",
                  "productExpiryDate": null,
                  "minimumStockLevel": null,
                  "taxRateId": "A",
                  "isProduct": true
                }
              ],
              "errors": []
            }
            """;

        var response = JsonSerializer.Deserialize<GetTerminalSiteProductsResponse>(
            json,
            MraJson.SerializerOptions);

        Assert.NotNull(response);
        Assert.True(response!.IsSuccess);
        Assert.Single(response.Data!);
        Assert.Null(response.Data[0].MinimumStockLevel);

        var parser = new PointOfSale.Mra.Services.TerminalSiteProductsResponseService(
            NullLogger<PointOfSale.Mra.Services.TerminalSiteProductsResponseService>.Instance);
        var parsed = parser.ParseJson(json);

        Assert.True(parsed.Success);
        Assert.Equal(0m, parsed.Snapshots[0].MinimumStockLevel);
    }

    [Fact]
    public void Parser_BuildsSnapshots_AndSyncPersistsLocalInventory()
    {
        const string json = """
            {
              "statusCode": 1,
              "remark": "Success",
              "data": [
                {
                  "productCode": "1234567890123",
                  "productName": "Coca Cola 500ml",
                  "description": "Carbonated soft drink",
                  "quantity": 120,
                  "unitOfMeasure": "Bottle",
                  "price": 1.5,
                  "siteId": "SITE001",
                  "productExpiryDate": "2025-12-31T00:00:00.000Z",
                  "minimumStockLevel": 10,
                  "taxRateId": "A",
                  "isProduct": true
                },
                {
                  "productCode": "SRV001",
                  "productName": "Car Wash Service",
                  "description": "Standard car wash",
                  "quantity": 0,
                  "unitOfMeasure": "Service",
                  "price": 10,
                  "siteId": "SITE001",
                  "productExpiryDate": null,
                  "minimumStockLevel": 0,
                  "taxRateId": "E",
                  "isProduct": false
                },
                {
                  "productCode": null,
                  "productName": "Broken row",
                  "quantity": 1,
                  "price": 1,
                  "isProduct": true
                }
              ],
              "errors": []
            }
            """;

        var parser = new PointOfSale.Mra.Services.TerminalSiteProductsResponseService(
            NullLogger<PointOfSale.Mra.Services.TerminalSiteProductsResponseService>.Instance);
        var parsed = parser.ParseJson(json);

        Assert.True(parsed.Success);
        Assert.Equal(3, parsed.ProductCount);
        Assert.Equal(2, parsed.UsableCount);
        Assert.Equal(1, parsed.SkippedInvalidRows);
        Assert.Equal(1, parsed.ServiceCount);
        Assert.NotNull(parsed.Snapshots[0].ProductExpiryDate);
        Assert.Null(parsed.Snapshots[1].ProductExpiryDate);

        var inventory = new FakeLocalInventoryRepository();
        var config = new FakeConfigurationRepository();
        var sync = new TerminalSiteProductsCatalogSyncService(
            parser,
            inventory,
            config,
            NullLogger<TerminalSiteProductsCatalogSyncService>.Instance);

        var result = sync.SyncFromJsonAsync(json, "2005000001", "SITE001").GetAwaiter().GetResult();
        Assert.True(result.Success);
        Assert.Equal(2, result.UpsertedCount);
        Assert.Equal(1, result.ProductCount);
        Assert.Equal(1, result.ServiceCount);

        var cola = inventory.GetByProductCodeAsync("1234567890123").GetAwaiter().GetResult();
        Assert.NotNull(cola);
        Assert.Equal("MraEis", cola!.CatalogSource);
        Assert.Equal("A", cola.TaxRateId);
        Assert.Equal(10m, cola.MinReorderQty);
    }

    [Fact]
    public void Parser_SkipsProductsWithoutPositivePrice()
    {
        const string json = """
            {
              "statusCode": 1,
              "remark": "Success",
              "data": [
                {
                  "productCode": "PRICED",
                  "productName": "Has price",
                  "quantity": 1,
                  "price": 100,
                  "isProduct": true
                },
                {
                  "productCode": "ZERO",
                  "productName": "Zero price",
                  "quantity": 5,
                  "price": 0,
                  "isProduct": true
                }
              ],
              "errors": []
            }
            """;

        var parser = new PointOfSale.Mra.Services.TerminalSiteProductsResponseService(
            NullLogger<PointOfSale.Mra.Services.TerminalSiteProductsResponseService>.Instance);
        var parsed = parser.ParseJson(json);

        Assert.True(parsed.Success);
        Assert.Equal(2, parsed.ProductCount);
        Assert.Equal(1, parsed.UsableCount);
        Assert.Equal(1, parsed.SkippedInvalidRows);
        Assert.Equal("PRICED", parsed.Snapshots[0].ProductCode);
        Assert.Equal(100m, parsed.Snapshots[0].UnitPrice);
    }

    [Fact]
    public async Task GetTerminalSiteProducts_PostsTinSiteId_WithBearerAndAcceptTextPlain()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;

        using var handler = new RecordingHandler(async (request, _) =>
        {
            captured = request;
            capturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

            var json = """
                {"statusCode":1,"remark":"Success","data":[{"productCode":"P1","productName":"Item","quantity":5,"unitOfMeasure":"Each","price":100,"siteId":"SITE-001","minimumStockLevel":1,"taxRateId":"A","isProduct":true}],"errors":[]}
                """;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://dev-eis-api.mra.mw/api/v1/")
        };
        var options = Options.Create(new MraApiOptions
        {
            Environment = "Sandbox",
            BaseUrl = "https://dev-eis-api.mra.mw/api/v1/"
        });
        var api = MraApiClient.CreateForTests(http, options, NullLogger<MraApiClient>.Instance);
        var auth = new TestMraTerminalAuthProvider
        {
            JwtToken = "raw-jwt-token",
            SecretKey = "secret"
        };
        var inventory = new FakeLocalInventoryRepository();
        var config = new FakeConfigurationRepository();
        var stock = new StockManagementService(
            api,
            auth,
            inventory,
            config,
            NullLogger<StockManagementService>.Instance,
            Options.Create(new PosOperationsOptions()));

        var result = await stock.GetTerminalSiteProductsAsync(
            new GetTerminalSiteProductsRequest { Tin = "2005000001", SiteId = "SITE-001" });

        Assert.True(result.Success);
        Assert.Single(result.Data!);
        Assert.Equal("P1", result.Data![0].ProductCode);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("utilities/get-terminal-site-products", captured.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(captured.Headers.TryGetValues("Authorization", out var authValues));
        Assert.Equal("Bearer raw-jwt-token", Assert.Single(authValues));
        Assert.True(captured.Headers.TryGetValues("Accept", out var acceptValues));
        Assert.Equal("text/plain", Assert.Single(acceptValues));
        Assert.Contains("\"tin\":\"2005000001\"", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"siteId\":\"SITE-001\"", capturedBody, StringComparison.Ordinal);
        Assert.False(captured.Headers.Contains(MraEisMessageHash.HeaderName));
        Assert.False(captured.Headers.Contains(HmacSignatureService.SignatureHeaderName));

        var cached = await config.GetJsonAsync(
            StockManagementService.BuildTerminalSiteProductsCacheKey("2005000001", "SITE-001"));
        Assert.False(string.IsNullOrWhiteSpace(cached));

        var local = await inventory.GetByProductCodeAsync("P1");
        Assert.NotNull(local);
        Assert.Equal("MraEis", local!.CatalogSource);
        Assert.Equal(100m, local.UnitPrice);
        Assert.Equal(5m, local.StockQuantity);
    }

    [Fact]
    public async Task GetTerminalSiteProducts_IntegrationMock_ReconcilesCatalog()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);

        var result = await harness.StockService.GetTerminalSiteProductsAsync(
            new GetTerminalSiteProductsRequest
            {
                Tin = "2005000001",
                SiteId = "SITE-001"
            });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Count);

        var cola = await harness.InventoryRepository.GetByProductCodeAsync("1234567890123");
        Assert.NotNull(cola);
        Assert.Equal("Carbonated soft drink", cola!.Name);
        Assert.Equal(1500m, cola.UnitPrice);
        Assert.Equal(10m, cola.MinReorderQty);

        var service = await harness.InventoryRepository.GetByProductCodeAsync("SRV001");
        Assert.NotNull(service);
        Assert.Equal("E", service!.TaxRateId);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) =>
            _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            _handler(request, cancellationToken);
    }
}
