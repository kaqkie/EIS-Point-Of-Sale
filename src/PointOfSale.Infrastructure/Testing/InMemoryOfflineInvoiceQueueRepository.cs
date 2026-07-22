using Microsoft.Data.SqlClient;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.Infrastructure.Testing;

public class InMemoryOfflineInvoiceQueueRepository : IOfflineInvoiceQueueRepository
{
    private readonly object _gate = new();
    private readonly List<OfflineInvoiceQueueItem> _items = new();
    private int _nextId = 1;

    public Task<int> EnqueuePendingAsync(string payloadJson, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var id = _nextId++;
            _items.Add(new OfflineInvoiceQueueItem
            {
                Id = id,
                PayloadJson = payloadJson,
                CreatedAt = DateTime.UtcNow,
                Status = OfflineQueueStatuses.Pending,
                RetryCount = 0
            });
            return Task.FromResult(id);
        }
    }

    public Task<int> EnqueuePendingAsync(
        string payloadJson,
        SqlConnection connection,
        SqlTransaction? transaction,
        CancellationToken cancellationToken = default) =>
        EnqueuePendingAsync(payloadJson, cancellationToken);

    public Task<OfflineInvoiceQueueItem?> GetNextFifoEligibleAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var utcNow = DateTime.UtcNow;
            var candidate = _items
                .Where(q => q.Status.Equals(OfflineQueueStatuses.Pending, StringComparison.OrdinalIgnoreCase))
                .Where(q => q.NextRetryTime is null || q.NextRetryTime <= utcNow)
                .Where(q => !_items.Any(blocker =>
                    (blocker.Status.Equals(OfflineQueueStatuses.Pending, StringComparison.OrdinalIgnoreCase) ||
                     blocker.Status.Equals(OfflineQueueStatuses.Syncing, StringComparison.OrdinalIgnoreCase)) &&
                    (blocker.CreatedAt < q.CreatedAt ||
                     (blocker.CreatedAt == q.CreatedAt && blocker.Id < q.Id))))
                .OrderBy(q => q.CreatedAt)
                .ThenBy(q => q.Id)
                .FirstOrDefault();

            return Task.FromResult(candidate);
        }
    }

    public Task<bool> TryMarkSyncingAsync(int id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(x => x.Id == id);
            if (item is null ||
                !item.Status.Equals(OfflineQueueStatuses.Pending, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(false);
            }

            item.Status = OfflineQueueStatuses.Syncing;
            return Task.FromResult(true);
        }
    }

    public Task MarkSyncedAsync(int id, string? fiscalResponseJson = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var item = _items.First(x => x.Id == id);
            item.Status = OfflineQueueStatuses.Synced;
            item.ErrorMessage = null;
            item.NextRetryTime = null;
            item.FiscalResponseJson = fiscalResponseJson;
            return Task.CompletedTask;
        }
    }

    public Task<OfflineInvoiceQueueItem?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.FirstOrDefault(x => x.Id == id));
        }
    }

    public Task<IReadOnlyList<OfflineInvoiceQueueItem>> GetItemsAsync(
        string? statusFilter,
        int take,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IEnumerable<OfflineInvoiceQueueItem> query = _items.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id);
            if (!string.IsNullOrWhiteSpace(statusFilter))
            {
                query = query.Where(x => x.Status.Equals(statusFilter, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult<IReadOnlyList<OfflineInvoiceQueueItem>>(query.Take(take).ToList());
        }
    }

    public Task MarkQuarantinedAsync(int id, string errorMessage, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var item = _items.First(x => x.Id == id);
            item.Status = OfflineQueueStatuses.Quarantined;
            item.ErrorMessage = errorMessage;
            item.NextRetryTime = null;
            return Task.CompletedTask;
        }
    }

    public Task MarkPendingRetryAsync(
        int id,
        int retryCount,
        DateTime nextRetryTimeUtc,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var item = _items.First(x => x.Id == id);
            item.Status = OfflineQueueStatuses.Pending;
            item.RetryCount = retryCount;
            item.NextRetryTime = nextRetryTimeUtc;
            item.ErrorMessage = errorMessage;
            return Task.CompletedTask;
        }
    }

    public Task ResetSyncingToPendingAsync(
        int id,
        int retryCount,
        DateTime nextRetryTimeUtc,
        string errorMessage,
        CancellationToken cancellationToken = default) =>
        MarkPendingRetryAsync(id, retryCount, nextRetryTimeUtc, errorMessage, cancellationToken);

    public Task<IReadOnlyDictionary<string, int>> GetStatusCountsAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyDictionary<string, int>>(
                _items
                    .GroupBy(x => x.Status, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase));
        }
    }

    public Task<IReadOnlyList<OfflineInvoiceQueueItem>> GetRecentItemsAsync(
        int take,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<OfflineInvoiceQueueItem>>(
                _items.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id).Take(take).ToList());
        }
    }

    public Task<bool> RetryQuarantinedAsync(int id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(x => x.Id == id);
            if (item is null ||
                !item.Status.Equals(OfflineQueueStatuses.Quarantined, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(false);
            }

            item.Status = OfflineQueueStatuses.Pending;
            item.RetryCount = 0;
            item.NextRetryTime = null;
            item.ErrorMessage = null;
            return Task.FromResult(true);
        }
    }
}
