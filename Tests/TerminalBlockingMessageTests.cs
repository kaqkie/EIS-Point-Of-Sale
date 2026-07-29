using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Contracts.Utilities;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;
using PointOfSale.Mra.Services;
using PointOfSale.Tests.Mocks;
using PointOfSale.Tests.Support;
using Xunit;

namespace PointOfSale.Tests;

public sealed class TerminalBlockingMessageTests
{
    private const string SampleBlockingJson = """
        {
          "statusCode": 1,
          "remark": "Terminal blocking message retrieved.",
          "data": {
            "isBlocked": true,
            "blockingReason": "Terminal blocked by MRA for compliance review. Contact MRA Taxpayer Services.",
            "blockedAt": "2025-05-28T06:42:59.980Z"
          },
          "errors": null
        }
        """;

    [Fact]
    public void Parser_MapsEnvelope_AndBuildsOperatorDisplay()
    {
        var parser = new TerminalBlockingMessageResponseService(
            NullLogger<TerminalBlockingMessageResponseService>.Instance);

        var parsed = parser.ParseJson(SampleBlockingJson);

        Assert.True(parsed.Success);
        Assert.True(parsed.IsBlocked);
        Assert.True(parsed.ShouldHaltSales);
        Assert.Contains("compliance review", parsed.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(parsed.BlockedAt);

        var display = parser.BuildOperatorDisplay(parsed);
        Assert.Equal("Terminal blocked by MRA", display.Title);
        Assert.True(display.ShouldHaltSales);
        Assert.Contains("Official MRA explanation", display.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2025-05-28", display.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_RejectsLogicalFailure_WithErrors()
    {
        const string failureJson = """
            {
              "statusCode": 0,
              "remark": "Unable to resolve blocking status.",
              "data": null,
              "errors": [
                { "errorCode": 40001, "fieldName": "terminalId", "errorMessage": "Unknown terminal" }
              ]
            }
            """;

        var parser = new TerminalBlockingMessageResponseService(
            NullLogger<TerminalBlockingMessageResponseService>.Instance);
        var parsed = parser.ParseJson(failureJson);

        Assert.False(parsed.Success);
        Assert.False(parsed.ShouldHaltSales);
        Assert.NotNull(parsed.Errors);
        Assert.Contains("Unknown terminal", parsed.ErrorDetail, StringComparison.OrdinalIgnoreCase);

        var display = parser.BuildOperatorDisplay(parsed);
        Assert.True(display.ShouldHaltSales);
        Assert.Equal(TerminalBlockingDisplaySeverity.Error, display.Severity);
    }

    [Fact]
    public void ProcessSuccessfulBlockingResponse_SurfacesHaltSalesUi()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);
        var service = new TerminalBlockingMessageService(
            harness.ApiClient,
            harness.AuthProvider,
            harness.ConfigurationRepository,
            NullLogger<TerminalBlockingMessageService>.Instance);

        var ui = service.ProcessSuccessfulBlockingResponse(SampleBlockingJson);

        Assert.True(ui.Success);
        Assert.True(ui.ShouldHaltSales);
        Assert.True(ui.IsBlocked);
        Assert.NotNull(ui.BlockedAt);
        Assert.Contains("compliance review", ui.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(ui.Display);
        Assert.Contains("stop all sales", ui.Display!.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTerminalBlockingMessage_PostsBearerAcceptAndTerminalId()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;

        using var handler = new RecordingHandler(async (request, _) =>
        {
            captured = request;
            capturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync().ConfigureAwait(false);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(SampleBlockingJson, Encoding.UTF8, "application/json")
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
        var runtime = new MraRuntimeEnvironmentState();
        var service = new TerminalBlockingMessageService(
            api,
            new TestMraTerminalAuthProvider { JwtToken = "raw-jwt-token", SecretKey = "secret", TerminalId = "TERM-BLOCK-001" },
            config,
            NullLogger<TerminalBlockingMessageService>.Instance,
            runtime);

        var result = await service.GetTerminalBlockingMessageAsync(
            new GetTerminalBlockingMessageRequest { TerminalId = "TERM-BLOCK-001" });

        Assert.True(result.Success);
        Assert.True(result.Data!.IsBlocked);
        Assert.True(result.ShouldHaltSales);
        Assert.NotNull(result.OperatorDisplay);
        Assert.Contains("compliance review", result.Data.BlockingReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.Data.BlockedAt);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("utilities/get-terminal-blocking-message", captured.RequestUri!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(captured.Headers.TryGetValues("Authorization", out var authValues));
        Assert.Equal("Bearer raw-jwt-token", Assert.Single(authValues));
        Assert.True(captured.Headers.TryGetValues("Accept", out var acceptValues));
        Assert.Equal("text/plain", Assert.Single(acceptValues));
        Assert.Contains("\"terminalId\":\"TERM-BLOCK-001\"", capturedBody, StringComparison.Ordinal);
        Assert.True(captured.Headers.Contains(MraEisMessageHash.HeaderName));
    }

    [Fact]
    public async Task HandleSalesResponse_WhenShouldBlockTerminal_FetchesMessageAndLocksTerminal()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);
        var runtime = new MraRuntimeEnvironmentState();
        var service = new TerminalBlockingMessageService(
            harness.ApiClient,
            harness.AuthProvider,
            harness.ConfigurationRepository,
            NullLogger<TerminalBlockingMessageService>.Instance,
            runtime);

        var handling = await service.HandleSalesResponseAsync(
            new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = "INV-BLOCK-1",
                ShouldBlockTerminal = true
            });

        Assert.True(handling.Required);
        Assert.True(handling.IsBlocked);
        Assert.Contains("blocked", handling.OperatorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(runtime.TerminalBlockedActive);
        Assert.NotNull(runtime.TerminalBlockingReason);

        var persistedJson = await harness.ConfigurationRepository
            .GetJsonAsync(MraConfigurationKeys.TerminalBlockingState);
        Assert.False(string.IsNullOrWhiteSpace(persistedJson));
        var snapshot = JsonSerializer.Deserialize<TerminalBlockingStateSnapshot>(
            persistedJson!,
            MraJson.SerializerOptions);
        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsBlocked);
        Assert.True(snapshot.TriggeredByShouldBlockTerminal);
    }

    [Fact]
    public async Task HandleSalesResponse_WhenShouldBoardTerminal_AlsoTriggersLockout()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);
        var runtime = new MraRuntimeEnvironmentState();
        var service = new TerminalBlockingMessageService(
            harness.ApiClient,
            harness.AuthProvider,
            harness.ConfigurationRepository,
            NullLogger<TerminalBlockingMessageService>.Instance,
            runtime);

        var handling = await service.HandleSalesResponseAsync(
            new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = "INV-BOARD-1",
                ShouldBoardTerminal = true
            });

        Assert.True(handling.IsBlocked);
        Assert.True(runtime.TerminalBlockedActive);
        var persistedJson = await harness.ConfigurationRepository
            .GetJsonAsync(MraConfigurationKeys.TerminalBlockingState);
        var snapshot = JsonSerializer.Deserialize<TerminalBlockingStateSnapshot>(
            persistedJson!,
            MraJson.SerializerOptions);
        Assert.True(snapshot!.TriggeredByShouldBoardTerminal);
    }

    [Fact]
    public async Task HandleSalesResponse_WhenFlagsFalse_DoesNothing()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);
        var runtime = new MraRuntimeEnvironmentState();
        var service = new TerminalBlockingMessageService(
            harness.ApiClient,
            harness.AuthProvider,
            harness.ConfigurationRepository,
            NullLogger<TerminalBlockingMessageService>.Instance,
            runtime);

        var handling = await service.HandleSalesResponseAsync(
            new SubmitSalesTransactionResponseData { InvoiceNumber = "INV-OK" });

        Assert.False(handling.Required);
        Assert.False(runtime.TerminalBlockedActive);
    }

    [Fact]
    public async Task GetTerminalBlockingMessage_IntegrationMock_Succeeds()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);
        var service = new TerminalBlockingMessageService(
            harness.ApiClient,
            harness.AuthProvider,
            harness.ConfigurationRepository,
            NullLogger<TerminalBlockingMessageService>.Instance);

        var result = await service.GetTerminalBlockingMessageAsync();
        Assert.True(result.Success);
        Assert.True(result.Data!.IsBlocked);
        Assert.False(string.IsNullOrWhiteSpace(result.Data.BlockingReason));
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
