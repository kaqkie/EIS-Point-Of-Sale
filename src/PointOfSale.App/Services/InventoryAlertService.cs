using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface IInventoryAlertService
{
    Task<InventoryAlertScanResult> ScanAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InventoryStockAlert>> GetOpenAlertsAsync(CancellationToken cancellationToken = default);
    Task AcknowledgeAsync(long alertId, CancellationToken cancellationToken = default);
    Task AcknowledgeAllAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, decimal>> GetAverageDailySalesAsync(
        int? lookbackDays = null,
        CancellationToken cancellationToken = default);
    decimal ResolveMinReorder(LocalInventoryItem item);
    decimal ResolveMaxCapacity(LocalInventoryItem item);
}

public sealed class InventoryAlertScanResult
{
    public int ScannedProducts { get; init; }
    public int LowStockCount { get; init; }
    public int StockoutCount { get; init; }
    public int OverstockCount { get; init; }
    public int FastMovingCount { get; init; }
    public IReadOnlyList<InventoryStockAlert> OpenAlerts { get; init; } = Array.Empty<InventoryStockAlert>();
    public DateTime ScannedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Stock monitoring engine: low-stock, stockout, overstock, and fast-moving detection
/// against SQL Express LocalInventory + OfflineInvoiceQueue sales velocity.
/// </summary>
public sealed class InventoryAlertService : IInventoryAlertService
{
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly IInventoryStockAlertRepository _alertRepository;
    private readonly IOfflineInvoiceQueueRepository _queueRepository;
    private readonly ICashierShiftRepository _shiftRepository;
    private readonly InventoryAlertOptions _options;
    private readonly ILogger<InventoryAlertService> _logger;

    public InventoryAlertService(
        ILocalInventoryRepository inventoryRepository,
        IInventoryStockAlertRepository alertRepository,
        IOfflineInvoiceQueueRepository queueRepository,
        ICashierShiftRepository shiftRepository,
        IOptions<InventoryAlertOptions> options,
        ILogger<InventoryAlertService> logger)
    {
        _inventoryRepository = inventoryRepository;
        _alertRepository = alertRepository;
        _queueRepository = queueRepository;
        _shiftRepository = shiftRepository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<InventoryAlertScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var products = await _inventoryRepository.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var velocity = await GetAverageDailySalesAsync(_options.VelocityLookbackDays, cancellationToken)
            .ConfigureAwait(false);
        var openShift = await _shiftRepository.GetOpenShiftAsync(cancellationToken).ConfigureAwait(false);
        var activeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var low = 0;
        var stockout = 0;
        var overstock = 0;
        var fast = 0;

        foreach (var item in products)
        {
            var minReorder = ResolveMinReorder(item);
            var maxCapacity = ResolveMaxCapacity(item);
            velocity.TryGetValue(item.ProductCode, out var avgDaily);
            avgDaily = PosTaxCalculator.RoundMoney(avgDaily);

            if (item.StockQuantity <= 0)
            {
                stockout++;
                await RaiseAsync(
                        item,
                        InventoryAlertTypes.Stockout,
                        InventoryAlertSeverities.Critical,
                        threshold: 0,
                        avgDaily,
                        openShift?.ShiftId,
                        $"{item.Name} is out of stock.",
                        activeKeys,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (item.StockQuantity <= minReorder)
            {
                low++;
                await RaiseAsync(
                        item,
                        InventoryAlertTypes.LowStock,
                        InventoryAlertSeverities.Warning,
                        minReorder,
                        avgDaily,
                        openShift?.ShiftId,
                        $"{item.Name} is at {item.StockQuantity:N2} (reorder ≤ {minReorder:N2}).",
                        activeKeys,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (maxCapacity > 0 && item.StockQuantity > maxCapacity)
            {
                overstock++;
                await RaiseAsync(
                        item,
                        InventoryAlertTypes.Overstock,
                        InventoryAlertSeverities.Info,
                        maxCapacity,
                        avgDaily,
                        openShift?.ShiftId,
                        $"{item.Name} exceeds capacity {maxCapacity:N2} (on hand {item.StockQuantity:N2}).",
                        activeKeys,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (avgDaily >= _options.FastMovingDailyUnits)
            {
                fast++;
                await RaiseAsync(
                        item,
                        InventoryAlertTypes.FastMoving,
                        InventoryAlertSeverities.Warning,
                        _options.FastMovingDailyUnits,
                        avgDaily,
                        openShift?.ShiftId,
                        $"{item.Name} is fast-moving (~{avgDaily:N2}/day).",
                        activeKeys,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await _alertRepository.ClearStaleOpenAlertsAsync(activeKeys, cancellationToken).ConfigureAwait(false);
        var open = await _alertRepository.GetOpenAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Inventory alert scan complete: {Products} products, low={Low}, stockout={Stockout}, overstock={Overstock}, fast={Fast}.",
            products.Count,
            low,
            stockout,
            overstock,
            fast);

        return new InventoryAlertScanResult
        {
            ScannedProducts = products.Count,
            LowStockCount = low,
            StockoutCount = stockout,
            OverstockCount = overstock,
            FastMovingCount = fast,
            OpenAlerts = open,
            ScannedAtUtc = DateTime.UtcNow
        };
    }

    public Task<IReadOnlyList<InventoryStockAlert>> GetOpenAlertsAsync(CancellationToken cancellationToken = default) =>
        _alertRepository.GetOpenAsync(cancellationToken);

    public Task AcknowledgeAsync(long alertId, CancellationToken cancellationToken = default) =>
        _alertRepository.AcknowledgeAsync(alertId, cancellationToken);

    public Task AcknowledgeAllAsync(CancellationToken cancellationToken = default) =>
        _alertRepository.AcknowledgeAllOpenAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<string, decimal>> GetAverageDailySalesAsync(
        int? lookbackDays = null,
        CancellationToken cancellationToken = default)
    {
        var days = Math.Max(1, lookbackDays ?? _options.VelocityLookbackDays);
        var since = DateTime.UtcNow.AddDays(-days);
        var items = await _queueRepository.GetRecentItemsAsync(500, cancellationToken).ConfigureAwait(false);
        var totals = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in items.Where(i => i.CreatedAt >= since))
        {
            if (string.IsNullOrWhiteSpace(row.PayloadJson))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(row.PayloadJson);
                if (!doc.RootElement.TryGetProperty("invoiceLineItems", out var lines)
                    && !doc.RootElement.TryGetProperty("InvoiceLineItems", out lines))
                {
                    continue;
                }

                foreach (var line in lines.EnumerateArray())
                {
                    var code = line.TryGetProperty("productCode", out var pc)
                        ? pc.GetString()
                        : line.TryGetProperty("ProductCode", out var pc2) ? pc2.GetString() : null;
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        continue;
                    }

                    decimal qty = 0;
                    if (line.TryGetProperty("quantity", out var q))
                    {
                        qty = q.GetDecimal();
                    }
                    else if (line.TryGetProperty("Quantity", out var q2))
                    {
                        qty = q2.GetDecimal();
                    }

                    totals[code] = totals.GetValueOrDefault(code) + qty;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Skipping unreadable queue payload {Id} during velocity scan.", row.Id);
            }
        }

        return totals.ToDictionary(
            kv => kv.Key,
            kv => PosTaxCalculator.RoundMoney(kv.Value / days),
            StringComparer.OrdinalIgnoreCase);
    }

    public decimal ResolveMinReorder(LocalInventoryItem item) =>
        item.MinReorderQty > 0 ? item.MinReorderQty : _options.DefaultMinReorderQty;

    public decimal ResolveMaxCapacity(LocalInventoryItem item) =>
        item.MaxStockCapacity > 0 ? item.MaxStockCapacity : _options.DefaultMaxStockCapacity;

    private async Task RaiseAsync(
        LocalInventoryItem item,
        string alertType,
        string severity,
        decimal threshold,
        decimal avgDaily,
        int? shiftId,
        string message,
        HashSet<string> activeKeys,
        CancellationToken cancellationToken)
    {
        activeKeys.Add($"{item.ProductCode}|{alertType}");
        await _alertRepository.UpsertOpenAlertAsync(
                new InventoryStockAlert
                {
                    ProductCode = item.ProductCode,
                    ProductName = item.Name,
                    AlertType = alertType,
                    Severity = severity,
                    StockQuantity = item.StockQuantity,
                    ThresholdQty = threshold,
                    AverageDailySales = avgDaily,
                    SupplierCode = item.SupplierCode,
                    Message = message,
                    ShiftId = shiftId
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>Background scanner for continuous low-stock / capacity monitoring.</summary>
public sealed class InventoryAlertBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly InventoryAlertOptions _options;
    private readonly ILogger<InventoryAlertBackgroundService> _logger;

    public InventoryAlertBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptions<InventoryAlertOptions> options,
        ILogger<InventoryAlertBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("Inventory alert background scanner is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(15, _options.ScanIntervalSeconds));
        _logger.LogInformation("Inventory alert scanner started (interval {Interval}s).", interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var alerts = scope.ServiceProvider.GetRequiredService<IInventoryAlertService>();
                await alerts.ScanAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory alert scan iteration failed.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
