using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Billing;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Serialization;
using PointOfSale.Tests.Support;
using Xunit;

namespace PointOfSale.Tests;

public sealed class ReceiptIdentifierUpdateTests
{
    [Fact]
    public async Task UpdateReceiptIdentifiers_RewritesLegacyArt_AndClearsQuarantine()
    {
        var queue = new Support.InMemoryOfflineInvoiceQueueRepository();
        var sales = new SalesTransactionService(
            apiClient: null!,
            authProvider: new TestMraTerminalAuthProvider(),
            inventoryRepository: new Mock<ILocalInventoryRepository>().Object,
            stockManagementService: null!,
            logger: NullLogger<SalesTransactionService>.Instance);

        var service = new OfflineSalesQueueService(
            queue,
            sales,
            Options.Create(new OfflineSyncOptions()),
            NullLogger<OfflineSalesQueueService>.Instance);

        var legacy = new SubmitSalesTransactionRequest
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = "ART-20260724164619",
                InvoiceDateTime = new DateTime(2026, 7, 24, 14, 46, 21, DateTimeKind.Utc),
                SellerTin = "20162939",
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
                    ProductCode = "P1",
                    Description = "Item",
                    UnitPrice = 100,
                    Quantity = 1,
                    Total = 100,
                    TotalVat = 17.5m,
                    TaxRateId = MraTaxRateCodes.StandardVat
                }
            ],
            InvoiceSummary = new InvoiceSummaryDto
            {
                TaxBreakDown =
                [
                    new TaxBreakDownDto { RateId = MraTaxRateCodes.StandardVat, TaxableAmount = 100, TaxAmount = 17.5m }
                ],
                TotalVat = 17.5m,
                InvoiceTotal = 117.5m,
                AmountTendered = 117.5m
            }
        };

        var queueId = await queue.EnqueuePendingAsync(JsonSerializer.Serialize(legacy, MraJson.SerializerOptions));
        await queue.MarkQuarantinedAsync(queueId, "opaque sandbox error");

        var result = await service.UpdateReceiptIdentifiersAsync(
            queueId,
            new InvoiceGenerationRequest(
                TransactionDateUtc: legacy.InvoiceHeader.InvoiceDateTime,
                SellerTin: "20162939"));

        Assert.True(result.Success, result.Error);
        Assert.True(result.Rewritten);
        Assert.Equal(queueId, result.QueueId);
        Assert.True(MraInvoiceNumberGenerator.IsMraCompositeInvoiceNumber(result.InvoiceNumber));
        Assert.True(MraInvoiceNumberGenerator.TryGetEncodedTaxpayerId(result.InvoiceNumber, out var tin));
        Assert.Equal(20162939, tin);

        var stored = await queue.GetByIdAsync(queueId);
        Assert.NotNull(stored);
        Assert.Equal(OfflineQueueStatuses.Pending, stored!.Status);
        Assert.Equal(0, stored.RetryCount);
        Assert.Null(stored.ErrorMessage);

        var payload = JsonSerializer.Deserialize<SubmitSalesTransactionRequest>(stored.PayloadJson, MraJson.SerializerOptions);
        Assert.Equal(result.InvoiceNumber, payload!.InvoiceHeader.InvoiceNumber);
        Assert.Equal("20162939", payload.InvoiceHeader.SellerTin);
    }
}
