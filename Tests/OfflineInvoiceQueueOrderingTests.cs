using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Testing;
using Xunit;

namespace PointOfSale.Tests;

public sealed class OfflineInvoiceQueueOrderingTests
{
    [Fact]
    public async Task GetItemsAsync_ReturnsNewestTransactionsFirst()
    {
        var repo = new InMemoryOfflineInvoiceQueueRepository();
        await repo.EnqueuePendingAsync("""{"invoiceHeader":{"invoiceNumber":"INV-1"}}""");
        await repo.EnqueuePendingAsync("""{"invoiceHeader":{"invoiceNumber":"INV-2"}}""");

        var items = await repo.GetItemsAsync(statusFilter: null, take: 10);

        Assert.Equal(2, items.Count);
        Assert.Equal("INV-2", ExtractInvoiceNumber(items[0]));
        Assert.Equal("INV-1", ExtractInvoiceNumber(items[1]));
        Assert.True(items[0].Id > items[1].Id);
    }

    [Fact]
    public async Task GetItemsAsync_StatusFilter_StillOrdersNewestFirst()
    {
        var repo = new InMemoryOfflineInvoiceQueueRepository();
        var olderId = await repo.EnqueuePendingAsync("""{"invoiceHeader":{"invoiceNumber":"P1"}}""");
        var newerId = await repo.EnqueuePendingAsync("""{"invoiceHeader":{"invoiceNumber":"P2"}}""");
        await repo.MarkQuarantinedAsync(olderId, "fail");

        var pending = await repo.GetItemsAsync(OfflineQueueStatuses.Pending, take: 10);
        var quarantined = await repo.GetItemsAsync(OfflineQueueStatuses.Quarantined, take: 10);

        Assert.Single(pending);
        Assert.Equal(newerId, pending[0].Id);
        Assert.Single(quarantined);
        Assert.Equal(olderId, quarantined[0].Id);
    }

    private static string? ExtractInvoiceNumber(OfflineInvoiceQueueItem item)
    {
        const string marker = "\"invoiceNumber\":\"";
        var start = item.PayloadJson.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = item.PayloadJson.IndexOf('"', start);
        return end < 0 ? null : item.PayloadJson[start..end];
    }
}
