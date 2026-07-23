using PointOfSale.App.ViewModels;
using PointOfSale.Core.Constants;
using Xunit;

namespace PointOfSale.Tests;

public sealed class QueueSyncStatusCommandTests
{
    [Theory]
    [InlineData(OfflineQueueStatuses.Quarantined, true, true, true)]
    [InlineData(OfflineQueueStatuses.Pending, false, true, true)]
    [InlineData(OfflineQueueStatuses.Synced, false, false, true)]
    [InlineData(OfflineQueueStatuses.Syncing, false, false, false)]
    public void QueueItem_ActionAvailability_MatchesStatus(
        string status,
        bool canRetry,
        bool canForceSync,
        bool canPrint)
    {
        var item = new QueueItemViewModel
        {
            Id = 7,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            PayloadJson = """{"invoiceHeader":{"invoiceNumber":"ART-1"}}"""
        };

        Assert.Equal(canRetry, item.CanRetry);
        Assert.Equal(canForceSync, item.CanForceSync);
        Assert.Equal(canPrint, item.CanPrintReceipt);
    }

    [Fact]
    public void QueueItem_EmptyPayload_CannotPrint()
    {
        var item = new QueueItemViewModel
        {
            Id = 1,
            Status = OfflineQueueStatuses.Synced,
            CreatedAt = DateTime.UtcNow,
            PayloadJson = "   "
        };

        Assert.False(item.CanPrintReceipt);
    }
}
