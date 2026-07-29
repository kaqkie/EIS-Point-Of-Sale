using System.Net.Http;
using System.Text.Json;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Inventory;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Billing;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Security;
using PointOfSale.Mra.Serialization;
using PointOfSale.Tests.Mocks;
using PointOfSale.Tests.Support;
using Xunit;

namespace PointOfSale.Tests;

public sealed class MraIntegrationTests
{
    [Fact]
    public void HmacSha512_MatchesMraBase64Standard_ForKnownVector()
    {
        const string plainText = "{\"invoiceNumber\":\"ART-0001\"}";
        const string secretKey = "TerminalSecretKey-IntegrationTest";

        var expected = Convert.ToBase64String(
            System.Security.Cryptography.HMACSHA512.HashData(
                System.Text.Encoding.UTF8.GetBytes(secretKey),
                System.Text.Encoding.UTF8.GetBytes(plainText)));

        var actual = HmacSignatureService.ComputeHmacSha512(plainText, secretKey);
        Assert.Equal(expected, actual);
        Assert.Equal(actual, HmacSignatureService.ComputeHmacSha512Base64(plainText, secretKey));
        Assert.Equal(actual, MraApiClient.ComputeSignature(plainText, secretKey));
        Assert.Equal(actual, HmacSignatureService.EncodeSignatureBase64(
            HmacSignatureService.ComputeHmacSha512Digest(plainText, secretKey)));
    }

    [Fact]
    public void ActivationConfirmationSignature_SignsTerminalActivationCode()
    {
        const string tac = "TAC-998877";
        const string secret = "activation-secret";

        var signature = HmacSignatureService.ComputeActivationConfirmationSignature(tac, secret);
        Assert.Equal(HmacSignatureService.ComputeHmacSha512(tac, secret), signature);
    }

    [Fact]
    public void AttachActivationConfirmationSignature_InjectsXSignatureHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "onboarding/terminal-activated-confirmation");
        var signature = HmacSignatureService.AttachActivationConfirmationSignature(
            request,
            terminalActivationCode: " TAC-112233 ",
            secretKey: "pending-secret");

        Assert.True(request.Headers.TryGetValues(HmacSignatureService.SignatureHeaderName, out var values));
        Assert.Equal(signature, Assert.Single(values));
        Assert.Equal(
            HmacSignatureService.ComputeActivationConfirmationSignature("TAC-112233", "pending-secret"),
            signature);
    }

    [Fact]
    public async Task TerminalActivatedConfirmation_AttachesXSignatureOverTac()
    {
        using var mock = new MockMraServer();
        mock.ConfigureCertificationEndpoints();
        using var harness = new MraIntegrationHarness(mock);

        const string tac = "TAC-CONFIRM-001";
        const string secret = "pending-activation-secret";

        var response = await harness.ApiClient.PostAsync<
            PointOfSale.Mra.Contracts.Onboarding.TerminalActivatedConfirmationRequest,
            bool>(
            "onboarding/terminal-activated-confirmation",
            new PointOfSale.Mra.Contracts.Onboarding.TerminalActivatedConfirmationRequest
            {
                TerminalId = "TERM-001"
            },
            new MraRequestContext
            {
                SecretKey = secret,
                SignaturePlainText = tac,
                IsActivationConfirmationSignature = true
            });

        Assert.True(response.IsSuccess);
        Assert.True(response.Data);

        var logged = mock.AllRequests.Last(r =>
            r.Path.Contains("terminal-activated-confirmation", StringComparison.OrdinalIgnoreCase));
        Assert.True(logged.Headers.TryGetValue(HmacSignatureService.SignatureHeaderName, out var sigValues));
        Assert.Equal(
            HmacSignatureService.ComputeHmacSha512(tac, secret),
            Assert.Single(sigValues));
        // Must sign the TAC — not the JSON body.
        Assert.NotEqual(
            HmacSignatureService.ComputeHmacSha512(logged.Body!, secret),
            Assert.Single(sigValues));
    }

    [Fact]
    public async Task OnlineSubmission_SendsXSignatureMatchingSerializedBody()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);

        var request = SalePayloadFactory.Create("SIG-CHECK-001");
        harness.InventoryRepository.Seed(SalePayloadFactory.DefaultProduct);

        await harness.SalesService.SubmitSalesTransactionAsync(request);

        var logged = mock.SalesRequests.Last();
        var sentSignature = logged.Headers
            .First(h => h.Key.Equals(HmacSignatureService.SignatureHeaderName, StringComparison.OrdinalIgnoreCase))
            .Value.First();

        var expected = HmacSignatureService.ComputeHmacSha512Base64(
            logged.Body!,
            harness.AuthProvider.SecretKey);

        Assert.Equal(expected, sentSignature);
    }

    [Fact]
    public async Task GetLastSubmittedOnline_PostsEmptyBody_WithBearerAuth_WithoutXSignature()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);

        var result = await harness.SalesService.GetLastSubmittedOnlineTransactionAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("20162939", result.Data!.InvoiceHeader?.SellerTin);

        var logged = mock.AllRequests.Last(r =>
            r.Path.Contains("last-submitted-online-transaction", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(string.Empty, logged.Body);
        Assert.False(logged.Headers.ContainsKey(HmacSignatureService.SignatureHeaderName));
        Assert.True(logged.Headers.TryGetValue("Authorization", out var authValues));
        Assert.StartsWith("Bearer ", Assert.Single(authValues), StringComparison.Ordinal);
        Assert.True(logged.Headers.TryGetValue("Accept", out var acceptValues));
        Assert.Equal("text/plain", Assert.Single(acceptValues));
    }

    [Fact]
    public async Task OfflineSignatureBackup_MatchesHmacSha256ValidationUrlParams()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);
        harness.InventoryRepository.Seed(SalePayloadFactory.DefaultProduct);

        var date = new DateTime(2024, 4, 26, 14, 29, 34, DateTimeKind.Utc);
        var invoice = MraInvoiceNumberGenerator.Generate(20162939, 1, date, 1);
        var baseRequest = SalePayloadFactory.Create(invoice);
        var request = baseRequest with
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = invoice,
                InvoiceDateTime = date,
                SellerTin = "20162939",
                SiteId = baseRequest.InvoiceHeader.SiteId,
                PaymentMethod = baseRequest.InvoiceHeader.PaymentMethod,
                GlobalConfigVersion = baseRequest.InvoiceHeader.GlobalConfigVersion,
                TaxpayerConfigVersion = baseRequest.InvoiceHeader.TaxpayerConfigVersion,
                TerminalConfigVersion = baseRequest.InvoiceHeader.TerminalConfigVersion
            }
        };

        var signed = await harness.SalesService.ComputeOfflineReceiptSignatureAsync(request);
        var expected = MraOfflineReceiptSigning.GenerateFromSalesRequest(
            request,
            harness.AuthProvider.SecretKey);

        Assert.Equal(expected.OfflineDataSignature, signed.OfflineDataSignature);
        Assert.Equal(expected.ValidationUrl, signed.ValidationUrl);
        Assert.Equal(expected.ParameterString, signed.ParameterString);
    }

    [Fact]
    public void InventoryUploadBatchPlanner_SplitsInto50ItemBatches_WithCorrectIsLastBatchFlags()
    {
        var items = Enumerable.Range(1, 120).Select(i => $"SKU-{i:D4}").ToList();
        var batches = InventoryUploadBatchPlanner.CreateBatches(items, maxBatchSize: 50);

        Assert.Equal(3, batches.Count);
        Assert.Equal(50, batches[0].Items.Count);
        Assert.Equal(50, batches[1].Items.Count);
        Assert.Equal(20, batches[2].Items.Count);
        Assert.False(batches[0].IsLastBatch);
        Assert.False(batches[1].IsLastBatch);
        Assert.True(batches[2].IsLastBatch);
        Assert.Equal(1, batches[0].BatchNumber);
        Assert.Equal(3, batches[2].BatchNumber);
    }

    [Fact]
    public void PosTaxCalculator_17_5PercentVat_AvoidsFloatingPointDrift()
    {
        const decimal unitPrice = 19.99m;
        const decimal quantity = 3m;
        const decimal rate = PosTaxCalculator.MalawiStandardVatRatePercent;

        var (net, vat, gross) = PosTaxCalculator.MapUnitPriceLine(unitPrice, quantity, rate);

        Assert.Equal(59.97m, net);
        Assert.Equal(10.49m, vat);
        Assert.Equal(70.46m, gross);
        Assert.Equal(PosTaxCalculator.CalculateLineTotal(unitPrice, quantity, rate), gross);
    }

    [Fact]
    public async Task UploadInitialInventoryInBatches_PostsThreeBatches_LastPayloadMarksIsLastBatch()
    {
        using var mock = new MockMraServer();
        mock.ConfigureInitialInventorySuccess();
        using var harness = new MraIntegrationHarness(mock);

        var items = Enumerable.Range(1, 120)
            .Select(i => new Mra.Contracts.Stock.InitialInventoryItemDto
            {
                BarCode = $"P{i:D4}",
                ProductName = $"Product {i}",
                ProductDescription = $"Product {i}",
                UnitPrice = 10m,
                QuantityInStock = 5,
                CostPrice = 10m,
                SellingPrice = 10m
            })
            .ToList();

        var result = await harness.StockService.UploadInitialInventoryInBatchesAsync(items);
        Assert.True(result.Success);
        Assert.Equal(120, result.Data!.UploadedItemCount);
        Assert.Equal(3, result.Data.BatchCount);
        Assert.NotNull(result.Data.FinalBatch);
        Assert.Contains("Synchronize Now", result.Remark, StringComparison.OrdinalIgnoreCase);

        var bodies = mock.InitialInventoryRequests
            .Select(x => x.Body!)
            .ToList();
        Assert.Equal(3, bodies.Count);

        using var first = JsonDocument.Parse(bodies[0]);
        using var last = JsonDocument.Parse(bodies[2]);
        Assert.False(first.RootElement.GetProperty("isLastBatch").GetBoolean());
        Assert.True(last.RootElement.GetProperty("isLastBatch").GetBoolean());
        Assert.Equal(50, first.RootElement.GetProperty("products").GetArrayLength());
        Assert.Equal(20, last.RootElement.GetProperty("products").GetArrayLength());

        // One-time: second attempt must be rejected locally.
        var second = await harness.StockService.UploadInitialInventoryInBatchesAsync(items.Take(1).ToList());
        Assert.False(second.Success);
        Assert.Contains("one-time", second.Remark, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfflineQueue_Http400_QuarantinesRecord_AndDoesNotBlockSubsequentFifoItems()
    {
        using var mock = new MockMraServer();
        mock.ConfigureSalesHttp400ForInvoice("INV-BAD-001");
        using var harness = new MraIntegrationHarness(mock);

        harness.InventoryRepository.Seed(SalePayloadFactory.DefaultProduct);

        var badId = await harness.QueueRepository.EnqueuePendingAsync(
            JsonSerializer.Serialize(SalePayloadFactory.Create("INV-BAD-001"), MraJson.SerializerOptions));
        var goodAId = await harness.QueueRepository.EnqueuePendingAsync(
            JsonSerializer.Serialize(SalePayloadFactory.Create("INV-GOOD-002"), MraJson.SerializerOptions));
        var goodBId = await harness.QueueRepository.EnqueuePendingAsync(
            JsonSerializer.Serialize(SalePayloadFactory.Create("INV-GOOD-003"), MraJson.SerializerOptions));

        Assert.True(await harness.OfflineQueueService.ProcessNextFifoAsync());
        Assert.True(await harness.OfflineQueueService.ProcessNextFifoAsync());
        Assert.True(await harness.OfflineQueueService.ProcessNextFifoAsync());

        var bad = await harness.QueueRepository.GetByIdAsync(badId);
        var goodA = await harness.QueueRepository.GetByIdAsync(goodAId);
        var goodB = await harness.QueueRepository.GetByIdAsync(goodBId);

        Assert.Equal(OfflineQueueStatuses.Quarantined, bad!.Status);
        Assert.Equal(OfflineQueueStatuses.Synced, goodA!.Status);
        Assert.Equal(OfflineQueueStatuses.Synced, goodB!.Status);
        Assert.NotNull(goodA.FiscalResponseJson);
        Assert.Contains("FSIG-SANDBOX", goodA.FiscalResponseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OfflineQueue_TransitionsPendingToSynced_OnSuccessfulFifoProcessing()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);

        var queueId = await harness.QueueRepository.EnqueuePendingAsync(
            JsonSerializer.Serialize(SalePayloadFactory.Create("INV-OK-100"), MraJson.SerializerOptions));

        var pending = await harness.QueueRepository.GetByIdAsync(queueId);
        Assert.Equal(OfflineQueueStatuses.Pending, pending!.Status);

        Assert.True(await harness.OfflineQueueService.ProcessNextFifoAsync());

        var synced = await harness.QueueRepository.GetByIdAsync(queueId);
        Assert.Equal(OfflineQueueStatuses.Synced, synced!.Status);
        Assert.NotNull(synced.FiscalResponseJson);
    }

    [Fact]
    public async Task InMemoryFifoRepository_TransitionsThroughSyncing_BeforeSyncComplete()
    {
        var repo = new InMemoryOfflineInvoiceQueueRepository();
        var id = await repo.EnqueuePendingAsync("{}");

        var next = await repo.GetNextFifoEligibleAsync();
        Assert.NotNull(next);
        Assert.True(await repo.TryMarkSyncingAsync(id));

        var syncing = await repo.GetByIdAsync(id);
        Assert.Equal(OfflineQueueStatuses.Syncing, syncing!.Status);

        await repo.MarkSyncedAsync(id, fiscalResponseJson: "{}");
        var synced = await repo.GetByIdAsync(id);
        Assert.Equal(OfflineQueueStatuses.Synced, synced!.Status);
    }

    [Fact]
    public async Task MockMraServer_SimulatedTimeout_SchedulesRetryInsteadOfQuarantine()
    {
        using var mock = new MockMraServer();
        mock.ConfigureSalesTimeout(TimeSpan.FromSeconds(5));
        using var harness = new MraIntegrationHarness(mock, httpTimeout: TimeSpan.FromMilliseconds(500));

        var queueId = await harness.QueueRepository.EnqueuePendingAsync(
            JsonSerializer.Serialize(SalePayloadFactory.Create("INV-TIMEOUT"), MraJson.SerializerOptions));

        await harness.OfflineQueueService.ProcessNextFifoAsync();

        var item = await harness.QueueRepository.GetByIdAsync(queueId);
        Assert.Equal(OfflineQueueStatuses.Pending, item!.Status);
        Assert.Equal(1, item.RetryCount);
        Assert.NotNull(item.NextRetryTime);
        Assert.NotEqual(OfflineQueueStatuses.Quarantined, item.Status);
    }
}

internal static class SalePayloadFactory
{
    public static LocalInventoryItem DefaultProduct => new()
    {
        ProductId = "PROD-001",
        ProductCode = "SKU-TEST",
        Name = "Test Product",
        UnitPrice = 100m,
        StockQuantity = 100,
        TaxRateId = "A",
        HsCode = "1234",
        UnitOfMeasure = "EA"
    };

    public static SubmitSalesTransactionRequest Create(string invoiceNumber) =>
        new()
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = invoiceNumber,
                InvoiceDateTime = DateTime.UtcNow,
                SellerTin = "1234567890",
                SiteId = "SITE-01",
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
                    UnitPrice = 100m,
                    Quantity = 1,
                    Total = 100m,
                    TotalVat = 17.5m,
                    TaxRateId = "A",
                    IsProduct = true
                }
            ],
            InvoiceSummary = new InvoiceSummaryDto
            {
                TaxBreakDown =
                [
                    new TaxBreakDownDto { RateId = "A", TaxableAmount = 100m, TaxAmount = 17.5m }
                ],
                TotalVat = 17.5m,
                InvoiceTotal = 117.5m,
                AmountTendered = 120m
            }
        };
}
