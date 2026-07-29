namespace PointOfSale.Core.Inventory;

/// <summary>
/// Splits large initial-inventory uploads into MRA-compliant batches (default max 50 items per batch).
/// Products remain in EIS staging until the last batch; warehouse stock requires portal Synchronize Now + approval.
/// </summary>
public static class InventoryUploadBatchPlanner
{
    public const int DefaultMaxBatchSize = 50;

    public static IReadOnlyList<InventoryUploadBatch<T>> CreateBatches<T>(
        IReadOnlyList<T> items,
        int maxBatchSize = DefaultMaxBatchSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (maxBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBatchSize), "Batch size must be positive.");
        }

        if (items.Count == 0)
        {
            return Array.Empty<InventoryUploadBatch<T>>();
        }

        var batchCount = (items.Count + maxBatchSize - 1) / maxBatchSize;
        var batches = new List<InventoryUploadBatch<T>>(batchCount);
        for (var batchIndex = 0; batchIndex < batchCount; batchIndex++)
        {
            var offset = batchIndex * maxBatchSize;
            var length = Math.Min(maxBatchSize, items.Count - offset);
            var slice = new T[length];
            for (var i = 0; i < length; i++)
            {
                slice[i] = items[offset + i];
            }

            batches.Add(new InventoryUploadBatch<T>(
                slice,
                BatchNumber: batchIndex + 1,
                IsLastBatch: batchIndex == batchCount - 1));
        }

        return batches;
    }
}

public sealed record InventoryUploadBatch<T>(
    IReadOnlyList<T> Items,
    int BatchNumber,
    bool IsLastBatch);
