using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Billing;
using PointOfSale.Mra.Options;
using PointOfSale.Tests.Support;
using Xunit;

namespace PointOfSale.Tests;

public sealed class LastSubmittedOfflineTransactionTests
{
    [Fact]
    public void TryParseComposite_ReadsTransactionCountSegment()
    {
        var invoice = MraInvoiceNumberGenerator.Generate(
            taxpayerId: 20162939,
            terminalPosition: 1,
            transactionDateUtc: new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
            transactionCount: 12);

        Assert.True(MraInvoiceNumberGenerator.TryParseComposite(invoice, out var parts));
        Assert.Equal(20162939, parts.TaxpayerId);
        Assert.Equal(1, parts.TerminalPosition);
        Assert.Equal(12, parts.TransactionCount);
    }

    [Fact]
    public async Task GetLastSubmittedOfflineTransaction_PostsEmptyBody_WithBearerAndAcceptTextPlain()
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
                {"statusCode":1,"remark":"ok","data":{"invoiceHeader":{"invoiceNumber":"BM6l7-B-B-M","sellerTIN":"20162939","siteId":"SITE-01","paymentMethod":"Cash","globalConfigVersion":1,"taxpayerConfigVersion":1,"terminalConfigVersion":1},"invoiceLineItems":[],"invoiceSummary":{"taxBreakDown":[],"totalVAT":0,"invoiceTotal":0,"amountTendered":0},"dateSubmitted":"2026-07-29T08:00:00Z"}}
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
        var sales = new SalesTransactionService(
            api,
            auth,
            inventory,
            stock,
            NullLogger<SalesTransactionService>.Instance);

        var result = await sales.GetLastSubmittedOfflineTransactionAsync();

        Assert.True(result.Success);
        Assert.Equal("BM6l7-B-B-M", result.Data!.InvoiceHeader!.InvoiceNumber);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("last-submitted-offline-transaction", captured.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(string.Empty, capturedBody);
        Assert.True(captured.Headers.TryGetValues("Authorization", out var authValues));
        Assert.Equal("Bearer raw-jwt-token", Assert.Single(authValues));
        Assert.True(captured.Headers.TryGetValues("Accept", out var acceptValues));
        Assert.Equal("text/plain", Assert.Single(acceptValues));
    }

    [Fact]
    public async Task VerifyOfflineSequenceContinuity_ReturnsAlignedCounts()
    {
        var invoice = MraInvoiceNumberGenerator.Generate(
            taxpayerId: 20162939,
            terminalPosition: 1,
            transactionDateUtc: new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc),
            transactionCount: 7);

        using var handler = new RecordingHandler((_, _) =>
        {
            var json =
                "{\"statusCode\":1,\"remark\":\"ok\",\"data\":{\"invoiceHeader\":{\"invoiceNumber\":\"" +
                invoice +
                "\",\"sellerTIN\":\"20162939\",\"siteId\":\"SITE-01\",\"paymentMethod\":\"Cash\"},\"invoiceLineItems\":[],\"invoiceSummary\":{\"taxBreakDown\":[],\"totalVAT\":0,\"invoiceTotal\":0,\"amountTendered\":0},\"dateSubmitted\":\"2026-07-29T08:00:00Z\"}}";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
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
        var auth = new TestMraTerminalAuthProvider { JwtToken = "jwt", SecretKey = "secret" };
        var inventory = new FakeLocalInventoryRepository();
        var stock = new StockManagementService(
            api,
            auth,
            inventory,
            new FakeConfigurationRepository(),
            NullLogger<StockManagementService>.Instance,
            Options.Create(new PosOperationsOptions()));
        var sales = new SalesTransactionService(
            api,
            auth,
            inventory,
            stock,
            NullLogger<SalesTransactionService>.Instance);

        var continuity = await sales.VerifyOfflineSequenceContinuityAsync();

        Assert.True(continuity.Parsed);
        Assert.Equal(invoice, continuity.LastInvoiceNumber);
        Assert.Equal(7, continuity.LastTransactionCount);
        Assert.Equal(20162939, continuity.TaxpayerId);
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
