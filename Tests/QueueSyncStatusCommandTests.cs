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

    [Fact]
    public void ResolveTarget_PrefersCommandParameter_ThenSelectedItem()
    {
        var selected = new QueueItemViewModel
        {
            Id = 1008,
            Status = OfflineQueueStatuses.Pending,
            CreatedAt = DateTime.UtcNow,
            PayloadJson = """{"invoiceHeader":{"invoiceNumber":"ART-20260724111350"}}""",
            InvoiceNumber = "ART-20260724111350"
        };
        var row = new QueueItemViewModel
        {
            Id = 1009,
            Status = OfflineQueueStatuses.Quarantined,
            CreatedAt = DateTime.UtcNow,
            PayloadJson = """{"invoiceHeader":{"invoiceNumber":"ART-OTHER"}}""",
            InvoiceNumber = "ART-OTHER"
        };

        Assert.Same(row, QueueSyncStatusViewModel.ResolveTargetForTest(row, selected));
        Assert.Same(selected, QueueSyncStatusViewModel.ResolveTargetForTest(null, selected));
        Assert.Null(QueueSyncStatusViewModel.ResolveTargetForTest(null, null));
    }
}
