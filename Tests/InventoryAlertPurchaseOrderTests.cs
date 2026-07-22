using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;
using Xunit;

namespace PointOfSale.Tests;

public sealed class InventoryAlertPurchaseOrderTests
{
    [Fact]
    public void CalculateSuggestedQty_UsesVelocityAndRespectsCapacity()
    {
        var options = Options.Create(new InventoryAlertOptions
        {
            DefaultMinReorderQty = 5,
            TargetDaysOfCover = 10,
            DefaultMaxStockCapacity = 0
        });

        var alertService = new InventoryAlertService(
            new FakeInventoryRepository(),
            new FakeAlertRepository(),
            new FakeQueueRepository(),
            new FakeShiftRepository(),
            options,
            NullLogger<InventoryAlertService>.Instance);

        var poService = new PurchaseOrderGenerationService(
            new FakeInventoryRepository(),
            alertService,
            new FakeAlertRepository(),
            new FakeSupplierRepository(),
            new FakePurchaseOrderRepository(),
            options,
            NullLogger<PurchaseOrderGenerationService>.Instance);

        var item = new LocalInventoryItem
        {
            ProductId = "1",
            ProductCode = "SKU-1",
            Name = "Sugar 1kg",
            UnitPrice = 1500m,
            StockQuantity = 4m,
            MinReorderQty = 10m,
            MaxStockCapacity = 50m
        };

        // velocity 3/day * 10 days = 30 target; need 26; capacity room 46 → 26
        var qty = poService.CalculateSuggestedQty(item, averageDailySales: 3m);
        Assert.Equal(26m, qty);

        // Capacity caps restock when target exceeds remaining shelf space.
        item.StockQuantity = 20m;
        item.MaxStockCapacity = 25m;
        var capped = poService.CalculateSuggestedQty(item, averageDailySales: 3m);
        Assert.Equal(5m, capped);
    }

    [Fact]
    public void ResolveMinReorder_FallsBackToDefault()
    {
        var options = Options.Create(new InventoryAlertOptions { DefaultMinReorderQty = 7m });
        var service = new InventoryAlertService(
            new FakeInventoryRepository(),
            new FakeAlertRepository(),
            new FakeQueueRepository(),
            new FakeShiftRepository(),
            options,
            NullLogger<InventoryAlertService>.Instance);

        var item = new LocalInventoryItem
        {
            ProductId = "1",
            ProductCode = "A",
            Name = "A",
            MinReorderQty = 0
        };

        Assert.Equal(7m, service.ResolveMinReorder(item));
    }

    private sealed class FakeInventoryRepository : ILocalInventoryRepository
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

        public Task ApplyHeadOfficeCatalogAsync(
            LocalInventoryItem catalogItem,
            bool preserveLocalStock,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeAlertRepository : IInventoryStockAlertRepository
    {
        public Task UpsertOpenAlertAsync(InventoryStockAlert alert, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task AcknowledgeAsync(long alertId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AcknowledgeAllOpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<InventoryStockAlert>> GetOpenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventoryStockAlert>>(Array.Empty<InventoryStockAlert>());

        public Task<IReadOnlyList<InventoryStockAlert>> GetRecentAsync(int take = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventoryStockAlert>>(Array.Empty<InventoryStockAlert>());

        public Task ClearStaleOpenAlertsAsync(
            IReadOnlyCollection<string> stillActiveKeys,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeQueueRepository : IOfflineInvoiceQueueRepository
    {
        public Task<int> EnqueuePendingAsync(string payloadJson, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> EnqueuePendingAsync(
            string payloadJson,
            SqlConnection connection,
            SqlTransaction? transaction,
            CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<OfflineInvoiceQueueItem?> GetNextFifoEligibleAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<OfflineInvoiceQueueItem?>(null);

        public Task<bool> TryMarkSyncingAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task MarkSyncedAsync(int id, string? fiscalResponseJson = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<OfflineInvoiceQueueItem?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult<OfflineInvoiceQueueItem?>(null);

        public Task<IReadOnlyList<OfflineInvoiceQueueItem>> GetItemsAsync(
            string? statusFilter,
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OfflineInvoiceQueueItem>>(Array.Empty<OfflineInvoiceQueueItem>());

        public Task MarkQuarantinedAsync(int id, string errorMessage, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task MarkPendingRetryAsync(
            int id,
            int retryCount,
            DateTime nextRetryTimeUtc,
            string errorMessage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetSyncingToPendingAsync(
            int id,
            int retryCount,
            DateTime nextRetryTimeUtc,
            string errorMessage,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, int>> GetStatusCountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

        public Task<IReadOnlyList<OfflineInvoiceQueueItem>> GetRecentItemsAsync(
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OfflineInvoiceQueueItem>>(Array.Empty<OfflineInvoiceQueueItem>());

        public Task<bool> RetryQuarantinedAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class FakeShiftRepository : ICashierShiftRepository
    {
        public Task<CashierShift?> GetOpenShiftAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<CashierShift?>(null);

        public Task<CashierShift?> GetByIdAsync(int shiftId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CashierShift?>(null);

        public Task<int> OpenShiftAsync(string cashierName, decimal openingFloat, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task CloseShiftAsync(
            int shiftId,
            decimal closingCashCounted,
            decimal expectedCash,
            decimal variance,
            string zReportJson,
            string? notes,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> AddCashMovementAsync(
            int shiftId,
            string movementType,
            decimal amount,
            string? reason,
            CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<IReadOnlyList<ShiftCashMovement>> GetMovementsAsync(
            int shiftId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ShiftCashMovement>>(Array.Empty<ShiftCashMovement>());

        public Task<IReadOnlyList<CashierShift>> GetRecentShiftsAsync(
            int take,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CashierShift>>(Array.Empty<CashierShift>());
    }

    private sealed class FakeSupplierRepository : IInventorySupplierRepository
    {
        public Task<IReadOnlyList<InventorySupplier>> GetAllAsync(
            bool activeOnly = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InventorySupplier>>(Array.Empty<InventorySupplier>());

        public Task UpsertAsync(InventorySupplier supplier, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakePurchaseOrderRepository : IPurchaseOrderRepository
    {
        public Task<long> CreateAsync(
            PurchaseOrder order,
            IReadOnlyList<PurchaseOrderLine> lines,
            CancellationToken cancellationToken = default) => Task.FromResult(1L);

        public Task MarkExportedAsync(long poId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateStatusAsync(long poId, string status, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PurchaseOrder>> GetRecentAsync(
            int take = 50,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PurchaseOrder>>(Array.Empty<PurchaseOrder>());

        public Task<PurchaseOrder?> GetByIdAsync(long poId, CancellationToken cancellationToken = default) =>
            Task.FromResult<PurchaseOrder?>(null);

        public Task<IReadOnlyList<PurchaseOrderLine>> GetLinesAsync(
            long poId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PurchaseOrderLine>>(Array.Empty<PurchaseOrderLine>());
    }
}
