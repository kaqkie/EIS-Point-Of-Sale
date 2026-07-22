using System.Text.Json;
using Moq;
using PointOfSale.App.Services;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Testing;
using PointOfSale.Mra.Serialization;
using Xunit;

namespace PointOfSale.Tests;

/// <summary>
/// End-to-end retail lifecycle integration tests against the MRA sandbox harness (no live EIS).
/// </summary>
public sealed class PointOfSaleIntegrationTests
{
    [Fact]
    public async Task RetailLifecycle_17_5Vat_CashTender_Change_CommitsToQueueAndSyncs()
    {
        using var sandbox = new MraSandboxSimulationHarness();
        var harness = sandbox.Integration;
        sandbox.MockServer.ConfigureSalesSuccessForAll();

        const decimal rate = PosTaxCalculator.MalawiStandardVatRatePercent;
        var (net, vat, gross) = PosTaxCalculator.MapUnitPriceLine(19.99m, 3m, rate);
        Assert.Equal(10.49m, vat);
        Assert.Equal(70.46m, gross);

        const decimal tender = 100m;
        var change = tender - gross;
        Assert.Equal(29.54m, change);

        var sale = SandboxSaleFactory.CreateOnlineSale("E2E-CASH-001", quantity: 3m, tenderMwk: tender);
        var queueResult = await harness.OfflineQueue.EnqueueAndTrySubmitAsync(sale, forceOffline: false);

        Assert.True(queueResult.SubmittedOnline);
        Assert.False(string.IsNullOrWhiteSpace(queueResult.InvoiceNumber));

        var item = await harness.Queue.GetByIdAsync(queueResult.QueueId);
        Assert.Equal(OfflineQueueStatuses.Synced, item!.Status);
        Assert.Contains("FSIG", item.FiscalResponseJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var product = await harness.Inventory.GetByProductCodeAsync(SandboxSaleFactory.DefaultProduct.ProductCode);
        Assert.NotNull(product);
        Assert.Equal(497m, product.StockQuantity);
    }

    [Fact]
    public async Task RetailLifecycle_ForceOffline_EnqueuesThenRecoversOnNetworkRestore()
    {
        using var sandbox = new MraSandboxSimulationHarness();
        var harness = sandbox.Integration;
        sandbox.MockServer.ConfigureSalesSuccessForAll();

        var sale = SandboxSaleFactory.CreateOnlineSale("E2E-OFFLINE-001");
        var queued = await harness.OfflineQueue.EnqueueAndTrySubmitAsync(sale, forceOffline: true);
        Assert.False(queued.SubmittedOnline);

        var pending = await harness.Queue.GetByIdAsync(queued.QueueId);
        Assert.Equal(OfflineQueueStatuses.Pending, pending!.Status);

        Assert.True(await harness.OfflineQueue.ProcessNextFifoAsync());
        var synced = await harness.Queue.GetByIdAsync(queued.QueueId);
        Assert.Equal(OfflineQueueStatuses.Synced, synced!.Status);
    }

    [Fact]
    public async Task SandboxHarness_StandardSuite_AllScenariosPass()
    {
        using var sandbox = new MraSandboxSimulationHarness();
        var report = await sandbox.RunStandardSuiteAsync();
        Assert.True(report.AllPassed, report.FailureLog);
        Assert.True(report.PassCount >= 5);
    }

    [Fact]
    public void ConnectionStatus_Moq_SimulatesMraUnreachableForOfflinePath()
    {
        var connection = new Mock<IConnectionStatusService>();
        connection.SetupGet(c => c.IsMraReachable).Returns(false);
        connection.SetupGet(c => c.IsOnline).Returns(true);

        Assert.False(connection.Object.IsMraReachable);
        connection.VerifyGet(c => c.IsMraReachable, Times.AtLeastOnce);
    }

    [Fact]
    public async Task IntegrationReport_SerializesForComplianceReadiness()
    {
        using var sandbox = new MraSandboxSimulationHarness();
        var report = await sandbox.RunStandardSuiteAsync();
        var json = JsonSerializer.Serialize(report);
        Assert.Contains("PassCount", json);
        Assert.Contains("Scenarios", json);
    }
}
