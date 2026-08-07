using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;

namespace PointOfSale.Infrastructure.Testing;

public enum MraSandboxScenario
{
    OnlineSubmissionWithValidHmac,
    SimulatedNetworkTimeout,
    OfflineQueueRecovery,
    InvalidVatFormatting,
    MismatchedHmacToken,
    InvalidVatThenSuccessfulRecovery
}

public sealed class IntegrationScenarioResult
{
    public required string Name { get; init; }
    public bool Passed { get; init; }
    public string Message { get; init; } = string.Empty;
    public long DurationMs { get; init; }
}

public sealed class IntegrationSuiteReport
{
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedUtc { get; set; }
    public IList<IntegrationScenarioResult> Scenarios { get; } = new List<IntegrationScenarioResult>();
    public int PassCount => Scenarios.Count(s => s.Passed);
    public int FailCount => Scenarios.Count(s => !s.Passed);
    public bool AllPassed => FailCount == 0 && Scenarios.Count > 0;
    public string FailureLog => string.Join(
        Environment.NewLine,
        Scenarios.Where(s => !s.Passed).Select(s => $"[{s.Name}] {s.Message}"));
}

/// <summary>
/// MRA EIS sandbox simulation harness — drives mock server fixtures for POS resilience testing.
/// </summary>
public class MraSandboxSimulationHarness : IDisposable
{
    private readonly MockMraEisServer _server;
    private readonly SandboxIntegrationHarness _harness;

    public MraSandboxSimulationHarness(TimeSpan? httpTimeout = null)
    {
        _server = new MockMraEisServer();
        _server.ConfigureCertificationEndpoints();
        _server.EnableHmacVerification(SandboxIntegrationHarness.DefaultSecretKey, rejectInvalidSignatures: true);
        _harness = new SandboxIntegrationHarness(_server, httpTimeout);
        _harness.Inventory.Seed(SandboxSaleFactory.DefaultProduct);
    }

    public MockMraEisServer MockServer => _server;

    public SandboxIntegrationHarness Integration => _harness;

    public async Task<IntegrationSuiteReport> RunStandardSuiteAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new IntegrationSuiteReport();
        var scenarios = new[]
        {
            MraSandboxScenario.OnlineSubmissionWithValidHmac,
            MraSandboxScenario.SimulatedNetworkTimeout,
            MraSandboxScenario.OfflineQueueRecovery,
            MraSandboxScenario.InvalidVatFormatting,
            MraSandboxScenario.MismatchedHmacToken
        };

        foreach (var scenario in scenarios)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Running {scenario}...");
            var result = await RunScenarioAsync(scenario, cancellationToken).ConfigureAwait(false);
            report.Scenarios.Add(result);
        }

        report.CompletedUtc = DateTime.UtcNow;
        return report;
    }

    public Task<IntegrationScenarioResult> RunScenarioAsync(
        MraSandboxScenario scenario,
        CancellationToken cancellationToken = default)
    {
        ResetSandboxBaseline();
        return scenario switch
        {
            MraSandboxScenario.OnlineSubmissionWithValidHmac => RunOnlineHmacScenarioAsync(cancellationToken),
            MraSandboxScenario.SimulatedNetworkTimeout => RunTimeoutScenarioAsync(cancellationToken),
            MraSandboxScenario.OfflineQueueRecovery => RunOfflineRecoveryScenarioAsync(cancellationToken),
            MraSandboxScenario.InvalidVatFormatting => RunInvalidVatScenarioAsync(cancellationToken),
            MraSandboxScenario.MismatchedHmacToken => RunMismatchedHmacScenarioAsync(cancellationToken),
            MraSandboxScenario.InvalidVatThenSuccessfulRecovery => RunInvalidVatThenRecoveryAsync(cancellationToken),
            _ => Task.FromResult(new IntegrationScenarioResult
            {
                Name = scenario.ToString(),
                Passed = false,
                Message = "Unknown scenario."
            })
        };
    }

    private void ResetSandboxBaseline()
    {
        _server.ConfigureCertificationEndpoints();
        _server.EnableHmacVerification(SandboxIntegrationHarness.DefaultSecretKey, rejectInvalidSignatures: true);
        _harness.Inventory.Seed(SandboxSaleFactory.DefaultProduct);
    }

    private async Task<IntegrationScenarioResult> RunOnlineHmacScenarioAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _server.ConfigureSalesSuccessForAll();
            var sale = SandboxSaleFactory.CreateOnlineSale("SANDBOX-ONLINE-001", tenderMwk: 120m);
            var result = await _harness.Sales.SubmitSalesTransactionAsync(sale, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return Fail(nameof(MraSandboxScenario.OnlineSubmissionWithValidHmac), result.Remark ?? "Submit failed.", sw);
            }

            var logged = _server.SalesRequests.Last();
            var signature = logged.Headers
                .First(h => h.Key.Equals(HmacSignatureService.SignatureHeaderName, StringComparison.OrdinalIgnoreCase))
                .Value.First();
            var expected = HmacSignatureService.ComputeHmacSha512Base64(
                logged.Body!,
                _harness.Auth.SecretKey);
            if (!string.Equals(signature, expected, StringComparison.Ordinal))
            {
                return Fail(nameof(MraSandboxScenario.OnlineSubmissionWithValidHmac), "HMAC mismatch on wire.", sw);
            }

            return Pass(nameof(MraSandboxScenario.OnlineSubmissionWithValidHmac), "Online fiscalization OK.", sw);
        }
        catch (Exception ex)
        {
            return Fail(nameof(MraSandboxScenario.OnlineSubmissionWithValidHmac), ex.Message, sw);
        }
    }

    private async Task<IntegrationScenarioResult> RunTimeoutScenarioAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeoutHarness = new SandboxIntegrationHarness(_server, TimeSpan.FromMilliseconds(400));
            timeoutHarness.Inventory.Seed(SandboxSaleFactory.DefaultProduct);
            _server.ConfigureSalesTimeout(TimeSpan.FromSeconds(3));

            var payload = JsonSerializer.Serialize(
                SandboxSaleFactory.CreateOnlineSale("SANDBOX-TIMEOUT"),
                MraJson.SerializerOptions);
            var queueId = await timeoutHarness.Queue.EnqueuePendingAsync(payload, cancellationToken).ConfigureAwait(false);
            await timeoutHarness.OfflineQueue.ProcessNextFifoAsync(cancellationToken).ConfigureAwait(false);

            var item = await timeoutHarness.Queue.GetByIdAsync(queueId, cancellationToken).ConfigureAwait(false);
            if (item?.Status != OfflineQueueStatuses.Pending || item.RetryCount < 1)
            {
                return Fail(nameof(MraSandboxScenario.SimulatedNetworkTimeout), "Expected retry scheduling after timeout.", sw);
            }

            return Pass(nameof(MraSandboxScenario.SimulatedNetworkTimeout), "Timeout scheduled FIFO retry.", sw);
        }
        catch (Exception ex)
        {
            return Fail(nameof(MraSandboxScenario.SimulatedNetworkTimeout), ex.Message, sw);
        }
    }

    private async Task<IntegrationScenarioResult> RunOfflineRecoveryScenarioAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _server.ConfigureSalesSuccessForAll();
            _server.ConfigureSalesHttp400ForInvoice("SANDBOX-BAD-VAT");

            var badPayload = JsonSerializer.Serialize(
                SandboxSaleFactory.CreateOnlineSale("SANDBOX-BAD-VAT"),
                MraJson.SerializerOptions);
            var goodPayload = JsonSerializer.Serialize(
                SandboxSaleFactory.CreateOnlineSale("SANDBOX-GOOD-002"),
                MraJson.SerializerOptions);

            var badId = await _harness.Queue.EnqueuePendingAsync(badPayload, cancellationToken).ConfigureAwait(false);
            var goodId = await _harness.Queue.EnqueuePendingAsync(goodPayload, cancellationToken).ConfigureAwait(false);

            await _harness.OfflineQueue.ProcessNextFifoAsync(cancellationToken).ConfigureAwait(false);
            await _harness.OfflineQueue.ProcessNextFifoAsync(cancellationToken).ConfigureAwait(false);

            var bad = await _harness.Queue.GetByIdAsync(badId, cancellationToken).ConfigureAwait(false);
            var good = await _harness.Queue.GetByIdAsync(goodId, cancellationToken).ConfigureAwait(false);

            if (bad?.Status != OfflineQueueStatuses.Quarantined || good?.Status != OfflineQueueStatuses.Synced)
            {
                return Fail(nameof(MraSandboxScenario.OfflineQueueRecovery), "FIFO quarantine/sync states incorrect.", sw);
            }

            return Pass(nameof(MraSandboxScenario.OfflineQueueRecovery), "Quarantine + sync recovery validated.", sw);
        }
        catch (Exception ex)
        {
            return Fail(nameof(MraSandboxScenario.OfflineQueueRecovery), ex.Message, sw);
        }
    }

    private async Task<IntegrationScenarioResult> RunInvalidVatScenarioAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _server.ConfigureSalesInvalidVatFormatting("SANDBOX-VAT-ERR");
            var sale = SandboxSaleFactory.CreateOnlineSale("SANDBOX-VAT-ERR");
            var result = await _harness.Sales.SubmitSalesTransactionAsync(sale, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                return Fail(nameof(MraSandboxScenario.InvalidVatFormatting), "Expected MRA 400 for invalid VAT.", sw);
            }

            return Pass(nameof(MraSandboxScenario.InvalidVatFormatting), result.Remark ?? "VAT validation rejected.", sw);
        }
        catch (MraApiException ex) when (ex.HttpStatusCode is 400)
        {
            return Pass(nameof(MraSandboxScenario.InvalidVatFormatting), ex.Message, sw);
        }
        catch (Exception ex)
        {
            return Fail(nameof(MraSandboxScenario.InvalidVatFormatting), ex.Message, sw);
        }
    }

    private async Task<IntegrationScenarioResult> RunMismatchedHmacScenarioAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _server.ConfigureSalesMismatchedHmacResponse();
            _harness.Auth.UseWrongSignatureForNextRequest = true;
            var sale = SandboxSaleFactory.CreateOnlineSale("SANDBOX-HMAC-BAD");
            var result = await _harness.Sales.SubmitSalesTransactionAsync(sale, cancellationToken).ConfigureAwait(false);
            _harness.Auth.UseWrongSignatureForNextRequest = false;

            if (result.Success)
            {
                return Fail(nameof(MraSandboxScenario.MismatchedHmacToken), "Expected unauthorized for bad HMAC.", sw);
            }

            return Pass(nameof(MraSandboxScenario.MismatchedHmacToken), result.Remark ?? "HMAC rejected.", sw);
        }
        catch (MraApiException ex) when (ex.HttpStatusCode is 401)
        {
            return Pass(nameof(MraSandboxScenario.MismatchedHmacToken), ex.Message, sw);
        }
        catch (Exception ex)
        {
            return Fail(nameof(MraSandboxScenario.MismatchedHmacToken), ex.Message, sw);
        }
    }

    private async Task<IntegrationScenarioResult> RunInvalidVatThenRecoveryAsync(CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await RunInvalidVatScenarioAsync(cancellationToken).ConfigureAwait(false);
            _server.ConfigureSalesSuccessForAll();
            var sale = SandboxSaleFactory.CreateOnlineSale("SANDBOX-RECOVER-001");
            var result = await _harness.Sales.SubmitSalesTransactionAsync(sale, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                return Fail(nameof(MraSandboxScenario.InvalidVatThenSuccessfulRecovery), result.Remark ?? "Recovery submit failed.", sw);
            }

            return Pass(nameof(MraSandboxScenario.InvalidVatThenSuccessfulRecovery), "Recovery after VAT error OK.", sw);
        }
        catch (Exception ex)
        {
            return Fail(nameof(MraSandboxScenario.InvalidVatThenSuccessfulRecovery), ex.Message, sw);
        }
    }

    private static IntegrationScenarioResult Pass(string name, string message, Stopwatch sw) =>
        new() { Name = name, Passed = true, Message = message, DurationMs = sw.ElapsedMilliseconds };

    private static IntegrationScenarioResult Fail(string name, string message, Stopwatch sw) =>
        new() { Name = name, Passed = false, Message = message, DurationMs = sw.ElapsedMilliseconds };

    public void Dispose()
    {
        _server.Dispose();
        _harness.Dispose();
    }
}

public sealed class SandboxIntegrationHarness : IDisposable
{
    public const string DefaultSecretKey = "ART-Integration-Test-Secret-Key";
    public const string DefaultTerminalId = "TERM-SANDBOX-001";

    public SandboxIntegrationHarness(MockMraEisServer mockServer, TimeSpan? httpTimeout = null)
    {
        Queue = new InMemoryOfflineInvoiceQueueRepository();
        Inventory = new SandboxInventoryRepository();
        Auth = new SandboxMraAuthProvider();

        var httpClient = new HttpClient(mockServer.HttpHandler) { BaseAddress = new Uri(mockServer.BaseUrl) };
        var mraOptions = Microsoft.Extensions.Options.Options.Create(new MraApiOptions
        {
            BaseUrl = mockServer.BaseUrl,
            HttpTimeout = httpTimeout ?? TimeSpan.FromSeconds(30)
        });

        ApiClient = MraApiClient.CreateForTests(httpClient, mraOptions, NullLogger<MraApiClient>.Instance);
        var config = new SandboxConfigurationRepository();
        Stock = new StockManagementService(
            ApiClient,
            Auth,
            Inventory,
            config,
            NullLogger<StockManagementService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new PosOperationsOptions { InventoryUploadBatchSize = 50 }));

        Sales = new SalesTransactionService(
            ApiClient,
            Auth,
            Inventory,
            Stock,
            NullLogger<SalesTransactionService>.Instance);

        OfflineQueue = new OfflineSalesQueueService(
            Queue,
            Sales,
            Microsoft.Extensions.Options.Options.Create(new OfflineSyncOptions { MaxRetryAttempts = 3, BaseBackoffSeconds = 1, MaxBackoffSeconds = 5 }),
            NullLogger<OfflineSalesQueueService>.Instance);
    }

    public InMemoryOfflineInvoiceQueueRepository Queue { get; }
    public SandboxInventoryRepository Inventory { get; }
    public SandboxMraAuthProvider Auth { get; }
    public MraApiClient ApiClient { get; }
    public StockManagementService Stock { get; }
    public SalesTransactionService Sales { get; }
    public OfflineSalesQueueService OfflineQueue { get; }

    public void Dispose()
    {
    }
}

public static class SandboxSaleFactory
{
    public static LocalInventoryItem DefaultProduct => new()
    {
        ProductId = "PROD-SANDBOX",
        ProductCode = "SKU-SANDBOX",
        Name = "Sandbox Retail Item",
        UnitPrice = 19.99m,
        StockQuantity = 500,
        TaxRateId = MraTaxRateCodes.StandardVat,
        HsCode = "1234",
        UnitOfMeasure = "EA"
    };

    public static SubmitSalesTransactionRequest CreateOnlineSale(string invoiceNumber, decimal quantity = 3m, decimal? tenderMwk = null)
    {
        const decimal rate = PosTaxCalculator.MalawiStandardVatRatePercent;
        var inclusiveUnit = DefaultProduct.UnitPrice;
        var (net, vat, gross) = PosTaxCalculator.MapInclusiveUnitPriceLine(inclusiveUnit, quantity, rate);
        var exclusiveUnit = PosTaxCalculator.ExtractExclusiveUnitFromInclusive(inclusiveUnit, rate);
        var tender = tenderMwk ?? Math.Ceiling(gross / 10m) * 10m;

        return new SubmitSalesTransactionRequest
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = invoiceNumber,
                InvoiceDateTime = DateTime.UtcNow,
                SellerTin = "1234567890",
                SiteId = "SITE-SANDBOX",
                PaymentMethod = "Cash",
                GlobalConfigVersion = 1,
                TaxpayerConfigVersion = 1,
                TerminalConfigVersion = 1
            },
            InvoiceLineItems =
            [
                new InvoiceLineItemDto
                {
                    Id = 1,
                    ProductCode = DefaultProduct.ProductCode,
                    Description = DefaultProduct.Name,
                    UnitPrice = exclusiveUnit,
                    Quantity = quantity,
                    Total = net,
                    TotalVat = vat,
                    TaxRateId = MraTaxRateCodes.StandardVat,
                    IsProduct = true
                }
            ],
            InvoiceSummary = new InvoiceSummaryDto
            {
                TaxBreakDown =
                [
                    new TaxBreakDownDto { RateId = MraTaxRateCodes.StandardVat, TaxableAmount = net, TaxAmount = vat }
                ],
                TotalVat = vat,
                InvoiceTotal = gross,
                AmountTendered = tender
            }
        };
    }
}
