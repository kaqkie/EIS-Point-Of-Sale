using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Billing;
using PointOfSale.Mra.Contracts.Configuration;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Tests.Mocks;
using PointOfSale.Tests.Support;
using Xunit;

namespace PointOfSale.Tests;

public sealed class OfflineTransactionSyncTests
{
    [Fact]
    public void ComplianceValidator_Quarantines_WhenOlderThanMaxAge()
    {
        var validator = new OfflineTransactionComplianceValidator();
        var sale = SalePayloadFactory.Create("E-De-JYxh-B");
        sale = sale with
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = sale.InvoiceHeader.InvoiceNumber,
                InvoiceDateTime = DateTime.UtcNow.AddHours(-80),
                SellerTin = sale.InvoiceHeader.SellerTin,
                SiteId = sale.InvoiceHeader.SiteId,
                PaymentMethod = sale.InvoiceHeader.PaymentMethod,
                GlobalConfigVersion = 1,
                TaxpayerConfigVersion = 1,
                TerminalConfigVersion = 1
            },
            InvoiceSummary = sale.InvoiceSummary with
            {
                OfflineSignature = "signed-offline-hmac"
            }
        };

        var result = validator.ValidateForUpload(
            sale,
            new OfflineLimitDto { MaxTransactionAgeInHours = 72 },
            queuedAtUtc: DateTime.UtcNow.AddHours(-80));

        Assert.False(result.IsCompliant);
        Assert.True(result.ShouldQuarantine);
        Assert.Contains("exceeded allowed age", result.Remark, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComplianceValidator_Quarantines_WhenOfflineSignatureMissing()
    {
        var validator = new OfflineTransactionComplianceValidator();
        var sale = SalePayloadFactory.Create("INV-NOSIG");
        sale = sale with
        {
            InvoiceSummary = sale.InvoiceSummary with { OfflineSignature = null }
        };

        var result = validator.ValidateForUpload(
            sale,
            new OfflineLimitDto { MaxTransactionAgeInHours = 72 },
            queuedAtUtc: DateTime.UtcNow);

        Assert.False(result.IsCompliant);
        Assert.Contains("offlineSignature", result.Remark, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComplianceValidator_Accepts_FreshSignedOfflineSale()
    {
        var validator = new OfflineTransactionComplianceValidator();
        var sale = SalePayloadFactory.Create("INV-OK");
        sale = sale with
        {
            InvoiceSummary = sale.InvoiceSummary with { OfflineSignature = "abc123signature" }
        };

        var result = validator.ValidateForUpload(
            sale,
            new OfflineLimitDto { MaxTransactionAgeInHours = 72 },
            queuedAtUtc: DateTime.UtcNow.AddMinutes(-5));

        Assert.True(result.IsCompliant);
        Assert.False(result.ShouldQuarantine);
    }

    [Fact]
    public void ComplianceValidator_Rejects_WhenCumulativeOfflineAmountExceeded()
    {
        var validator = new OfflineTransactionComplianceValidator();
        var sale = SalePayloadFactory.Create("INV-BIG");
        sale = sale with
        {
            InvoiceSummary = sale.InvoiceSummary with
            {
                OfflineSignature = "sig",
                InvoiceTotal = 100_000m
            }
        };

        var result = validator.ValidateForUpload(
            sale,
            new OfflineLimitDto { MaxTransactionAgeInHours = 72, MaxCummulativeAmount = 750_000m },
            queuedAtUtc: DateTime.UtcNow,
            pendingOfflineCumulativeAmount: 700_000m);

        Assert.False(result.IsCompliant);
        Assert.Contains("cumulative", result.Remark, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComplianceValidator_BlocksNewOffline_WhenPendingAgeExceeded()
    {
        var validator = new OfflineTransactionComplianceValidator();
        var result = validator.ValidateCanContinueOffline(
            prospectiveInvoiceTotal: 1_000m,
            offlineLimit: new OfflineLimitDto { MaxTransactionAgeInHours = 72, MaxCummulativeAmount = 750_000m },
            pendingOfflineCumulativeAmount: 10_000m,
            oldestPendingQueuedAtUtc: DateTime.UtcNow.AddHours(-80));

        Assert.False(result.IsCompliant);
        Assert.Contains("exceeds allowed age", result.Remark, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ComplianceValidator_BlocksNewOffline_WhenWallClockOfflineWindowExceeded()
    {
        var validator = new OfflineTransactionComplianceValidator();
        var now = DateTime.UtcNow;
        var result = validator.ValidateCanContinueOffline(
            prospectiveInvoiceTotal: 1_000m,
            offlineLimit: new OfflineLimitDto { MaxTransactionAgeInHours = 72 },
            pendingOfflineCumulativeAmount: 0m,
            oldestPendingQueuedAtUtc: null,
            asOfUtc: now,
            lastMraReachableUtc: now.AddHours(-80));

        Assert.False(result.IsCompliant);
        Assert.Contains("offline from MRA", result.Remark, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("72", result.Remark);
    }

    [Fact]
    public void ComplianceValidator_HonoursMraChangedOfflineWindowHours()
    {
        var validator = new OfflineTransactionComplianceValidator();
        var now = DateTime.UtcNow;

        // MRA shortened the offline window to 24h — a 30h disconnect must be blocked.
        var blocked = validator.ValidateCanContinueOffline(
            prospectiveInvoiceTotal: 500m,
            offlineLimit: new OfflineLimitDto { MaxTransactionAgeInHours = 24 },
            pendingOfflineCumulativeAmount: 0m,
            oldestPendingQueuedAtUtc: null,
            asOfUtc: now,
            lastMraReachableUtc: now.AddHours(-30));

        Assert.False(blocked.IsCompliant);
        Assert.Contains("24", blocked.Remark);

        // Same disconnect is allowed again after MRA raises the window back to 72h.
        var allowed = validator.ValidateCanContinueOffline(
            prospectiveInvoiceTotal: 500m,
            offlineLimit: new OfflineLimitDto { MaxTransactionAgeInHours = 72 },
            pendingOfflineCumulativeAmount: 0m,
            oldestPendingQueuedAtUtc: null,
            asOfUtc: now,
            lastMraReachableUtc: now.AddHours(-30));

        Assert.True(allowed.IsCompliant);
        Assert.Equal(72, allowed.MaxAgeHours);
    }

    [Fact]
    public void ComplianceValidator_AllowsOffline_WithinWallClockWindow()
    {
        var validator = new OfflineTransactionComplianceValidator();
        var now = DateTime.UtcNow;
        var result = validator.ValidateCanContinueOffline(
            prospectiveInvoiceTotal: 1_000m,
            offlineLimit: new OfflineLimitDto { MaxTransactionAgeInHours = 72 },
            pendingOfflineCumulativeAmount: 0m,
            oldestPendingQueuedAtUtc: null,
            asOfUtc: now,
            lastMraReachableUtc: now.AddHours(-10));

        Assert.True(result.IsCompliant);
        Assert.Equal(72, result.MaxAgeHours);
    }

    [Fact]
    public void ResolveMaxAgeHours_FallsBackTo72_WhenMissing()
    {
        Assert.Equal(72, OfflineTransactionComplianceValidator.ResolveMaxAgeHours(null));
        Assert.Equal(72, OfflineTransactionComplianceValidator.ResolveMaxAgeHours(
            new OfflineLimitDto { MaxTransactionAgeInHours = 0 }));
        Assert.Equal(48, OfflineTransactionComplianceValidator.ResolveMaxAgeHours(
            new OfflineLimitDto { MaxTransactionAgeInHours = 48 }));
    }

    [Fact]
    public async Task DrainPending_Pauses_WhenMraUnreachable()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);
        var connectivity = new FakeConnectivityMonitor { IsMraReachable = false };
        var sync = new OfflineTransactionSyncService(
            harness.OfflineQueueService,
            harness.QueueRepository,
            new OfflineTransactionComplianceValidator(),
            connectivity,
            Options.Create(new OfflineSyncOptions
            {
                RequireMraConnectivity = true,
                EnforceTransactionAge = true,
                RequireOfflineSignature = true
            }),
            NullLogger<OfflineTransactionSyncService>.Instance,
            harness.ConfigurationRepository);

        var drain = await sync.DrainPendingAsync();
        Assert.True(drain.ConnectivityPaused);
        Assert.Equal(0, drain.ProcessedCount);
    }

    [Fact]
    public async Task PrepareAndValidate_AttachesOfflineSignature_UsingHmacSha256()
    {
        using var mock = new MockMraServer();
        using var harness = new MraIntegrationHarness(mock);
        var date = DateTime.UtcNow.AddMinutes(-10);
        var invoice = MraInvoiceNumberGenerator.Generate(20162939, 1, date, 1);
        var baseSale = SalePayloadFactory.Create(invoice);
        var sale = baseSale with
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = invoice,
                InvoiceDateTime = date,
                SellerTin = "20162939",
                SiteId = baseSale.InvoiceHeader.SiteId,
                PaymentMethod = baseSale.InvoiceHeader.PaymentMethod,
                GlobalConfigVersion = 1,
                TaxpayerConfigVersion = 1,
                TerminalConfigVersion = 1
            },
            InvoiceSummary = baseSale.InvoiceSummary with { OfflineSignature = null }
        };

        var offlineSigner = new OfflineReceiptSignatureService(
            harness.AuthProvider,
            Options.Create(new PointOfSale.Mra.Options.MraApiOptions { Environment = "Sandbox" }),
            NullLogger<OfflineReceiptSignatureService>.Instance);

        var sync = new OfflineTransactionSyncService(
            harness.OfflineQueueService,
            harness.QueueRepository,
            new OfflineTransactionComplianceValidator(),
            new AlwaysReachableMraConnectivityMonitor(),
            Options.Create(new OfflineSyncOptions
            {
                EnforceTransactionAge = true,
                RequireOfflineSignature = true,
                DefaultMaxTransactionAgeInHours = 72
            }),
            NullLogger<OfflineTransactionSyncService>.Instance,
            harness.ConfigurationRepository,
            offlineSigner);

        var prepared = await sync.PrepareAndValidateForUploadAsync(sale, DateTime.UtcNow);
        Assert.True(prepared.Accepted, prepared.RejectionRemark);
        Assert.False(string.IsNullOrWhiteSpace(prepared.Request.InvoiceSummary.OfflineSignature));

        var expected = MraOfflineReceiptSigning.GenerateFromSalesRequest(
            sale,
            harness.AuthProvider.SecretKey);
        Assert.Equal(expected.OfflineDataSignature, prepared.Request.InvoiceSummary.OfflineSignature);
    }

    private sealed class FakeConnectivityMonitor : IMraConnectivityMonitor
    {
        public bool IsMraReachable { get; set; }

        public event EventHandler? ReachabilityChanged;

        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Raise() => ReachabilityChanged?.Invoke(this, EventArgs.Empty);
    }
}
