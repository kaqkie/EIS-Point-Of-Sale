using System.Net.Http;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PointOfSale.Infrastructure.Http;
using PointOfSale.Mra.Security;
using PointOfSale.Tests.Mocks;
using PointOfSale.Tests.Support;
using Xunit;

namespace PointOfSale.Tests;

public sealed class MraEisMessageHashHandlerTests
{
    [Theory]
    [InlineData("https://dev-eis-api.mra.mw/api/v1/onboarding/activate-terminal", true)]
    [InlineData("onboarding/activate-terminal", true)]
    [InlineData("https://dev-eis-api.mra.mw/api/v1/onboarding/terminal-activated-confirmation", false)]
    [InlineData("sales/submit-sales-transaction", false)]
    [InlineData("configuration/get-latest-configs", false)]
    public void IsTerminalActivationPath_ExcludesOnlyActivateTerminal(string path, bool expected) =>
        Assert.Equal(expected, MraEisMessageHash.IsTerminalActivationPath(path));

    [Fact]
    public async Task Handler_AttachesMessageHash_ForSalesPayload()
    {
        const string body = "{\"invoiceNumber\":\"ART-1\"}";
        const string secret = "terminal-secret";
        HttpRequestMessage? captured = null;

        var services = new ServiceCollection().BuildServiceProvider();
        var handler = new MraEisMessageHashHandler(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MraEisMessageHashHandler>.Instance)
        {
            InnerHandler = new RecordingHandler((request, _) =>
            {
                captured = request;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"statusCode\":1}", Encoding.UTF8, "application/json")
                });
            })
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://dev-eis-api.mra.mw/api/v1/") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "sales/submit-sales-transaction")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        MraEisMessageHash.SetSecretKeyOption(request, secret);

        _ = await client.SendAsync(request);

        Assert.NotNull(captured);
        Assert.True(captured!.Headers.TryGetValues(MraEisMessageHash.HeaderName, out var values));
        Assert.Equal(MraEisMessageHash.Compute(body, secret), Assert.Single(values));
    }

    [Fact]
    public async Task Handler_SkipsMessageHash_ForActivateTerminal()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var handler = new MraEisMessageHashHandler(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<MraEisMessageHashHandler>.Instance)
        {
            InnerHandler = new RecordingHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"statusCode\":1}", Encoding.UTF8, "application/json")
                }))
        };

        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://dev-eis-api.mra.mw/api/v1/") };
        using var request = new HttpRequestMessage(HttpMethod.Post, "onboarding/activate-terminal")
        {
            Content = new StringContent("{\"terminalActivationCode\":\"TAC\"}", Encoding.UTF8, "application/json")
        };
        MraEisMessageHash.SetSecretKeyOption(request, "should-not-be-used");

        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode);
        Assert.False(request.Headers.Contains(MraEisMessageHash.HeaderName));
    }

    [Fact]
    public async Task OnlineSubmission_SendsXEisMessageHashMatchingBody()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);

        var sale = SalePayloadFactory.Create("MSG-HASH-001");
        harness.InventoryRepository.Seed(SalePayloadFactory.DefaultProduct);

        await harness.SalesService.SubmitSalesTransactionAsync(sale);

        var logged = mock.SalesRequests.Last();
        Assert.True(logged.Headers.TryGetValue(MraEisMessageHash.HeaderName, out var hashValues));
        Assert.Equal(
            MraEisMessageHash.Compute(logged.Body!, harness.AuthProvider.SecretKey),
            Assert.Single(hashValues));
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
