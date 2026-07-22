using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;
using Xunit;

namespace PointOfSale.Tests;

public sealed class GoodsReceiptTests
{
    [Fact]
    public void WeightedAverageCost_BlendsPriorStockAndReceipt()
    {
        var service = CreateGrnService();
        // (10 * 100) + (10 * 200) = 3000 / 20 = 150
        var wac = service.CalculateWeightedAverageCost(
            previousStock: 10m,
            previousAvgCost: 100m,
            receiveQty: 10m,
            unitCost: 200m);
        Assert.Equal(150m, wac);
    }

    [Fact]
    public void RetailPrice_AppliesMarkupOverWac()
    {
        var service = CreateGrnService();
        Assert.Equal(1250m, service.CalculateRetailPrice(1000m, markupPercent: 25m));
    }

    [Fact]
    public void TryScanBarcode_IncrementsMatchingLine()
    {
        var service = CreateGrnService();
        var draft = new GoodsReceiptDraft
        {
            PoId = 1,
            PoNumber = "PO-1",
            SupplierCode = "SUP1",
            SupplierName = "Supplier",
            Lines =
            [
                new GoodsReceiptLine
                {
                    ProductCode = "SKU-1",
                    ProductName = "Item",
                    OrderedQty = 5,
                    ReceivedQty = 0,
                    UnitCost = 100
                }
            ]
        };

        Assert.True(service.TryScanBarcode(draft, "SKU-1", quantity: 2));
        Assert.Equal(2m, draft.Lines[0].ReceivedQty);
        Assert.False(service.TryScanBarcode(draft, "UNKNOWN"));
    }

    private static GoodsReceiptService CreateGrnService() =>
        new(
            new FakePoRepository(),
            new FakeGrnRepository(),
            new FakeInvRepository(),
            Options.Create(new GoodsReceiptOptions
            {
                ApplyRetailMarkupOnReceipt = true,
                DefaultMarkupPercent = 25m
            }),
            NullLogger<GoodsReceiptService>.Instance);

    private sealed class FakePoRepository : IPurchaseOrderRepository
    {
        public Task<long> CreateAsync(PurchaseOrder order, IReadOnlyList<PurchaseOrderLine> lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(1L);

        public Task MarkExportedAsync(long poId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateStatusAsync(long poId, string status, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PurchaseOrder>> GetRecentAsync(int take = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PurchaseOrder>>(Array.Empty<PurchaseOrder>());

        public Task<IReadOnlyList<PurchaseOrder>> GetReceivableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PurchaseOrder>>(Array.Empty<PurchaseOrder>());

        public Task<PurchaseOrder?> GetByIdAsync(long poId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PurchaseOrder?>(null);

        public Task<IReadOnlyList<PurchaseOrderLine>> GetLinesAsync(long poId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PurchaseOrderLine>>(Array.Empty<PurchaseOrderLine>());
    }

    private sealed class FakeGrnRepository : IGoodsReceiptRepository
    {
        public Task<long> CreateAsync(GoodsReceiptNote grn, IReadOnlyList<GoodsReceiptLine> lines, CancellationToken cancellationToken = default) =>
            Task.FromResult(1L);

        public Task UpdateDraftAsync(GoodsReceiptNote grn, IReadOnlyList<GoodsReceiptLine> lines, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkPostedAsync(long grnId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<GoodsReceiptNote?> GetByIdAsync(long grnId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GoodsReceiptNote?>(null);

        public Task<IReadOnlyList<GoodsReceiptLine>> GetLinesAsync(long grnId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoodsReceiptLine>>(Array.Empty<GoodsReceiptLine>());

        public Task<IReadOnlyList<GoodsReceiptNote>> GetRecentAsync(int take = 50, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoodsReceiptNote>>(Array.Empty<GoodsReceiptNote>());

        public Task<IReadOnlyList<GoodsReceiptNote>> GetByPoIdAsync(long poId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoodsReceiptNote>>(Array.Empty<GoodsReceiptNote>());
    }

    private sealed class FakeInvRepository : ILocalInventoryRepository
    {
        public Task<IReadOnlyList<LocalInventoryItem>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalInventoryItem>>(Array.Empty<LocalInventoryItem>());

        public Task<LocalInventoryItem?> GetByProductCodeAsync(string productCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalInventoryItem?>(null);

        public Task<LocalInventoryItem?> GetByProductIdAsync(string productId, CancellationToken cancellationToken = default) =>
            Task.FromResult<LocalInventoryItem?>(null);

        public Task UpsertAsync(LocalInventoryItem item, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateReorderSettingsAsync(
            string productCode,
            decimal minReorderQty,
            decimal maxStockCapacity,
            string? supplierCode,
            string? supplierName,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ApplyGoodsReceiptAsync(
            string productCode,
            decimal goodQtyReceived,
            decimal unitCost,
            decimal newAverageUnitCost,
            decimal? newRetailPrice,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ApplyHeadOfficeCatalogAsync(
            LocalInventoryItem catalogItem,
            bool preserveLocalStock,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
