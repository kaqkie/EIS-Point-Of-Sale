using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.App.Services;
using PointOfSale.Core.Compliance;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Pricing;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Testing;
using PointOfSale.Mra.Contracts.Sales;
using Xunit;

namespace PointOfSale.Tests;

/// <summary>
/// Phase 34 — end-to-end cashier checkout lifecycle + supervisor authorization harness.
/// </summary>
public sealed class EndToEndCheckoutIntegrationTests
{
    [Fact]
    public async Task CheckoutLifecycle_ScanVatTender_EscPosReceipt_EntersOfflineQueue()
    {
        using var sandbox = new MraSandboxSimulationHarness();
        var harness = sandbox.Integration;
        sandbox.MockServer.ConfigureSalesSuccessForAll();

        // 1) Scan / select an inventory item
        var product = await harness.Inventory.GetByProductCodeAsync(SandboxSaleFactory.DefaultProduct.ProductCode);
        Assert.NotNull(product);
        Assert.Equal("SKU-SANDBOX", product.ProductCode);

        // 2) Calculate mandatory 17.5% Malawi VAT
        const decimal qty = 2m;
        const decimal rate = PosTaxCalculator.MalawiStandardVatRatePercent;
        Assert.Equal(17.5m, rate);
        var (net, vat, gross) = PosTaxCalculator.MapUnitPriceLine(product.UnitPrice, qty, rate);
        Assert.Equal(PosTaxCalculator.CalculateVatAmount(net, rate), vat);
        Assert.Equal(net + vat, gross);

        // 3) Process payment tendering
        const decimal tender = 100m;
        Assert.True(tender >= gross);
        var change = tender - gross;
        Assert.True(change >= 0);

        var sale = SandboxSaleFactory.CreateOnlineSale("E2E-P34-CHECKOUT-001", quantity: qty, tenderMwk: tender);
        Assert.Equal(gross, sale.InvoiceSummary.InvoiceTotal);
        Assert.Equal(vat, sale.InvoiceSummary.TotalVat);
        Assert.Equal(tender, sale.InvoiceSummary.AmountTendered);

        // Force offline path so the invoice remains visible in the sync queue for assertion,
        // then optionally recover — mirrors cashier “MRA unreachable” workflow.
        var queued = await harness.OfflineQueue.EnqueueAndTrySubmitAsync(sale, forceOffline: true);
        Assert.False(queued.SubmittedOnline);
        Assert.True(queued.QueueId > 0);

        var pending = await harness.Queue.GetByIdAsync(queued.QueueId);
        Assert.NotNull(pending);
        Assert.Equal(OfflineQueueStatuses.Pending, pending.Status);
        Assert.Contains("E2E-P34-CHECKOUT-001", pending.PayloadJson, StringComparison.Ordinal);

        // 4) ESC/POS receipt formatting with MRA verification URL (high-density QR)
        var fiscalResponse = new SubmitSalesTransactionResponseData
        {
            InvoiceNumber = "E2E-P34-CHECKOUT-001",
            FiscalCode = "FSIG-E2E-P34",
            VerificationUrl = "https://eis.mra.mw/verify/E2E-P34-CHECKOUT-001"
        };
        var receiptRequest = new ReceiptPrintRequest
        {
            TradingName = "Albert Retail Terminal",
            SellerTin = "1234567890",
            AddressLines = ["Lilongwe"],
            InvoiceNumber = "E2E-P34-CHECKOUT-001",
            InvoiceDateTime = DateTime.UtcNow,
            LineItems = sale.InvoiceLineItems,
            TaxBreakdown = sale.InvoiceSummary.TaxBreakDown,
            InvoiceTotal = gross,
            AmountTendered = tender,
            FiscalResponse = fiscalResponse
        };

        var escPos = EscPosReceiptEncoder.Encode(receiptRequest, charactersPerLine: 42, highDensityMraQr: true);
        Assert.True(escPos.Length > 64);
        Assert.Equal(0x1B, escPos[0]); // ESC @
        Assert.Equal(0x40, escPos[1]);
        var ascii = System.Text.Encoding.ASCII.GetString(escPos);
        Assert.Contains("VAT", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MRA EIS", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Scan to verify", ascii, StringComparison.OrdinalIgnoreCase);
        Assert.Contains((byte)0x08, escPos); // high-density module size
        Assert.Contains((byte)0x33, escPos); // ECC H

        // 5) Recover into synced state (offline → online sync)
        Assert.True(await harness.OfflineQueue.ProcessNextFifoAsync());
        var synced = await harness.Queue.GetByIdAsync(queued.QueueId);
        Assert.Equal(OfflineQueueStatuses.Synced, synced!.Status);
        Assert.False(string.IsNullOrWhiteSpace(synced.FiscalResponseJson));
    }

    [Fact]
    public async Task CheckoutLifecycle_OnlineSubmit_DecrementsStockAndCapturesFiscalSignature()
    {
        using var sandbox = new MraSandboxSimulationHarness();
        var harness = sandbox.Integration;
        sandbox.MockServer.ConfigureSalesSuccessForAll();

        var before = await harness.Inventory.GetByProductCodeAsync(SandboxSaleFactory.DefaultProduct.ProductCode);
        Assert.NotNull(before);
        var startingQty = before.StockQuantity;

        var sale = SandboxSaleFactory.CreateOnlineSale("E2E-P34-ONLINE-001", quantity: 1m, tenderMwk: 50m);
        var result = await harness.OfflineQueue.EnqueueAndTrySubmitAsync(sale, forceOffline: false);

        Assert.True(result.SubmittedOnline);
        Assert.False(string.IsNullOrWhiteSpace(result.InvoiceNumber));

        var after = await harness.Inventory.GetByProductCodeAsync(SandboxSaleFactory.DefaultProduct.ProductCode);
        Assert.Equal(startingQty - 1m, after!.StockQuantity);

        var item = await harness.Queue.GetByIdAsync(result.QueueId);
        Assert.Equal(OfflineQueueStatuses.Synced, item!.Status);
        Assert.Contains("FSIG", item.FiscalResponseJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SupervisorAuthorization_PinAndPassword_GrantRestrictedActions()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var (pwdHash, pwdSalt, pwdIter) = hasher.HashPassword("Supervisor!123");
        var (pinHash, pinSalt, pinIter) = hasher.HashPassword("2468");

        var supervisor = new OperatorAccount
        {
            OperatorId = 7,
            Username = "floor.supervisor",
            DisplayName = "Floor Supervisor",
            Role = OperatorRoles.Supervisor,
            PasswordHash = pwdHash,
            PasswordSalt = pwdSalt,
            PasswordIterations = pwdIter,
            IsActive = true,
            SupervisorPinHash = pinHash,
            SupervisorPinSalt = pinSalt,
            SupervisorPinIterations = pinIter
        };

        var operators = new Mock<IOperatorRepository>();
        operators.Setup(o => o.GetByUsernameAsync("floor.supervisor", It.IsAny<CancellationToken>()))
            .ReturnsAsync(supervisor);
        operators.Setup(o => o.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { supervisor });

        var scopeFactory = CreateScopeFactory(operators.Object);
        var auth = new Mock<IAuthenticationAuthorizationService>();
        auth.SetupGet(a => a.CurrentOperator).Returns((OperatorSession?)null);
        auth.Setup(a => a.HasPermission(It.IsAny<string>())).Returns(false);

        var securityAudit = new Mock<IAuditSecurityLogger>();
        var complianceAudit = new Mock<IComplianceAuditLogger>();

        var service = new SupervisorAuthorizationService(
            scopeFactory,
            auth.Object,
            hasher,
            securityAudit.Object,
            complianceAudit.Object,
            Options.Create(new SupervisorAuthorizationOptions()),
            NullLogger<SupervisorAuthorizationService>.Instance);

        var pinGrant = await service.AuthorizeAsync(new SupervisorOverrideRequest
        {
            ActionType = SupervisorOverrideActions.ItemVoid,
            RequiredPermission = OperatorPermissions.PerformVoid,
            Credential = "2468",
            AllowCurrentSession = false
        });
        Assert.True(pinGrant.Authorized);
        Assert.Equal("SupervisorPin", pinGrant.AuthorizationMode);

        var passwordGrant = await service.AuthorizeAsync(new SupervisorOverrideRequest
        {
            ActionType = SupervisorOverrideActions.DiscountLimitException,
            RequiredPermission = OperatorPermissions.ApplyCartDiscount,
            SupervisorUsername = "floor.supervisor",
            Credential = "Supervisor!123",
            AllowCurrentSession = false
        });
        Assert.True(passwordGrant.Authorized);
        Assert.Equal("SupervisorPassword", passwordGrant.AuthorizationMode);

        var denied = await service.AuthorizeAsync(new SupervisorOverrideRequest
        {
            ActionType = SupervisorOverrideActions.PostShiftReturn,
            RequiredPermission = OperatorPermissions.PerformVoid,
            Credential = "0000",
            AllowCurrentSession = false
        });
        Assert.False(denied.Authorized);

        securityAudit.Verify(
            a => a.LogAsync(
                SecurityAuditActions.SupervisorOverride,
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
        complianceAudit.Verify(
            a => a.LogEventAsync(
                ComplianceAuditCategories.SupervisorAuth,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task SupervisorAuthorization_SessionPermission_ShortCircuitsWithoutCredential()
    {
        var auth = new Mock<IAuthenticationAuthorizationService>();
        auth.Setup(a => a.HasPermission(OperatorPermissions.PerformVoid)).Returns(true);
        auth.SetupGet(a => a.CurrentOperator).Returns(new OperatorSession
        {
            OperatorId = 1,
            Username = "admin",
            DisplayName = "Admin",
            Role = OperatorRoles.Administrator,
            Permissions = RolePermissionCatalog.GetPermissions(OperatorRoles.Administrator),
            SignedInAtUtc = DateTime.UtcNow
        });

        var securityAudit = new Mock<IAuditSecurityLogger>();
        var complianceAudit = new Mock<IComplianceAuditLogger>();
        var operators = new Mock<IOperatorRepository>();
        var service = new SupervisorAuthorizationService(
            CreateScopeFactory(operators.Object),
            auth.Object,
            new Pbkdf2PasswordHasher(),
            securityAudit.Object,
            complianceAudit.Object,
            Options.Create(new SupervisorAuthorizationOptions()),
            NullLogger<SupervisorAuthorizationService>.Instance);

        var result = await service.AuthorizeAsync(new SupervisorOverrideRequest
        {
            ActionType = SupervisorOverrideActions.ItemVoid,
            RequiredPermission = OperatorPermissions.PerformVoid,
            AllowCurrentSession = true
        });

        Assert.True(result.Authorized);
        Assert.Equal("SessionPermission", result.AuthorizationMode);
        operators.Verify(o => o.GetAllAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static IServiceScopeFactory CreateScopeFactory(IOperatorRepository operators)
    {
        var services = new ServiceCollection();
        services.AddSingleton(operators);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IServiceScopeFactory>();
    }
}
