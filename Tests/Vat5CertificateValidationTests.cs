using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Security;
using PointOfSale.Tests.Mocks;
using PointOfSale.Tests.Support;
using Xunit;

namespace PointOfSale.Tests;

public sealed class Vat5CertificateValidationTests
{
    [Fact]
    public void ApplyReliefSupplyLine_RemovesStandardVat()
    {
        var (net, vat, gross) = PosTaxCalculator.ApplyReliefSupplyLine(
            unitPrice: 100m,
            quantity: 2m,
            ratePercent: 17.5m,
            isStandardVatTier: true);

        Assert.Equal(200m, net);
        Assert.Equal(0m, vat);
        Assert.Equal(200m, gross);
    }

    [Fact]
    public async Task ValidateVat5Certificate_PostsBearerAcceptAndTracksBalance()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;

        using var handler = new RecordingHandler(async (request, _) =>
        {
            captured = request;
            capturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

            const string json = """
                {"statusCode":1,"remark":"VAT 5 certificate validation succeeded.","data":{"projectNumber":"VATF/00000132/2024","certificateNumber":"MRA/BMTO/VAT5/000169","quantity":80,"dateOfIssue":"2024-02-23T00:00:00","dateOfExpiry":"2099-03-24T00:00:00"},"errors":null}
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
        var api = MraApiClient.CreateForTests(
            http,
            Options.Create(new MraApiOptions
            {
                Environment = "Sandbox",
                BaseUrl = "https://dev-eis-api.mra.mw/api/v1/"
            }),
            NullLogger<MraApiClient>.Instance);
        var config = new FakeConfigurationRepository();
        var service = new Vat5CertificateValidationService(
            api,
            new TestMraTerminalAuthProvider { JwtToken = "raw-jwt-token", SecretKey = "secret" },
            config,
            NullLogger<Vat5CertificateValidationService>.Instance);

        var result = await service.ValidateVat5CertificateAsync(
            new ValidateVat5CertificateRequest
            {
                ProjectNumber = "VATF/00000132/2024",
                CertificateNumber = "MRA/BMTO/VAT5/000169",
                Quantity = 20
            });

        Assert.True(result.Success);
        Assert.True(result.AllowsReliefSupply);
        Assert.Equal(80m, result.Certificate!.Quantity);
        Assert.Equal(80m, result.RemainingQuantity);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("utilities/validate-vat5-certificate", captured.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(captured.Headers.TryGetValues("Authorization", out var authValues));
        Assert.Equal("Bearer raw-jwt-token", Assert.Single(authValues));
        Assert.True(captured.Headers.TryGetValues("Accept", out var acceptValues));
        Assert.Equal("text/plain", Assert.Single(acceptValues));
        Assert.Contains("\"projectNumber\":\"VATF/00000132/2024\"", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"certificateNumber\":\"MRA/BMTO/VAT5/000169\"", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"quantity\":20", capturedBody, StringComparison.Ordinal);
        Assert.True(captured.Headers.Contains(MraEisMessageHash.HeaderName));

        var ledger = await service.RecordCertificateConsumptionAsync(
            "VATF/00000132/2024",
            "MRA/BMTO/VAT5/000169",
            quantityUsed: 20);
        Assert.Equal(20m, ledger.ConsumedQuantity);
        Assert.Equal(60m, ledger.RemainingQuantity);

        var again = await service.ValidateVat5CertificateAsync(
            new ValidateVat5CertificateRequest
            {
                ProjectNumber = "VATF/00000132/2024",
                CertificateNumber = "MRA/BMTO/VAT5/000169",
                Quantity = 20
            });
        Assert.True(again.AllowsReliefSupply);
        Assert.Equal(60m, again.RemainingQuantity);
    }

    [Fact]
    public void ApplyReliefSupplyToSalesRequest_ZerosStandardVat_AndSetsIsReliefSupply()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);
        var service = new Vat5CertificateValidationService(
            harness.ApiClient,
            harness.AuthProvider,
            harness.ConfigurationRepository,
            NullLogger<Vat5CertificateValidationService>.Instance);

        var sale = SalePayloadFactory.Create("VAT5-RELIEF-001");
        var relieved = service.ApplyReliefSupplyToSalesRequest(
            sale,
            new Vat5CertificateValidationData
            {
                ProjectNumber = "VATF/00000132/2024",
                CertificateNumber = "MRA/BMTO/VAT5/000169",
                Quantity = 80
            },
            usageQuantity: 1);

        Assert.True(relieved.InvoiceHeader.IsReliefSupply);
        Assert.Equal("VATF/00000132/2024", relieved.InvoiceHeader.Vat5CertificateDetails!.ProjectNumber);
        Assert.Equal(0m, relieved.InvoiceSummary.TotalVat);
        Assert.All(relieved.InvoiceLineItems, line => Assert.Equal(0m, line.TotalVat));
        Assert.True(relieved.InvoiceSummary.InvoiceTotal < sale.InvoiceSummary.InvoiceTotal);
    }

    [Fact]
    public async Task ValidateVat5Certificate_IntegrationMock_Succeeds()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);
        var service = new Vat5CertificateValidationService(
            harness.ApiClient,
            harness.AuthProvider,
            harness.ConfigurationRepository,
            NullLogger<Vat5CertificateValidationService>.Instance);

        var result = await service.ValidateVat5CertificateAsync(
            new ValidateVat5CertificateRequest
            {
                ProjectNumber = "VATF/00000132/2024",
                CertificateNumber = "MRA/BMTO/VAT5/000169",
                Quantity = 5
            });

        Assert.True(result.AllowsReliefSupply);
        Assert.Equal(80m, result.Certificate!.Quantity);
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
