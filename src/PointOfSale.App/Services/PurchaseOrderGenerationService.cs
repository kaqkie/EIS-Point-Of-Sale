using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface IPurchaseOrderGenerationService
{
    Task<PurchaseOrderGenerationResult> GenerateFromLowStockAsync(
        string? supplierFilter,
        string? operatorUsername,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PurchaseOrder>> GetRecentOrdersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PurchaseOrderLine>> GetLinesAsync(long poId, CancellationToken cancellationToken = default);
    Task<string> ExportCsvAsync(long poId, CancellationToken cancellationToken = default);
    Task ExportPdfAsync(long poId, CancellationToken cancellationToken = default);
    decimal CalculateSuggestedQty(LocalInventoryItem item, decimal averageDailySales);
}

public sealed class PurchaseOrderGenerationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string GeneratedPoSummary { get; init; } = string.Empty;
    public IReadOnlyList<PurchaseOrder> Orders { get; init; } = Array.Empty<PurchaseOrder>();
}

/// <summary>
/// Aggregates low-stock / stockout alerts into supplier-grouped purchase orders
/// using sales velocity and capacity-aware restock quantities.
/// </summary>
public sealed class PurchaseOrderGenerationService : IPurchaseOrderGenerationService
{
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly IInventoryAlertService _alertService;
    private readonly IInventoryStockAlertRepository _alertRepository;
    private readonly IInventorySupplierRepository _supplierRepository;
    private readonly IPurchaseOrderRepository _purchaseOrderRepository;
    private readonly InventoryAlertOptions _options;
    private readonly ILogger<PurchaseOrderGenerationService> _logger;

    public PurchaseOrderGenerationService(
        ILocalInventoryRepository inventoryRepository,
        IInventoryAlertService alertService,
        IInventoryStockAlertRepository alertRepository,
        IInventorySupplierRepository supplierRepository,
        IPurchaseOrderRepository purchaseOrderRepository,
        IOptions<InventoryAlertOptions> options,
        ILogger<PurchaseOrderGenerationService> logger)
    {
        _inventoryRepository = inventoryRepository;
        _alertService = alertService;
        _alertRepository = alertRepository;
        _supplierRepository = supplierRepository;
        _purchaseOrderRepository = purchaseOrderRepository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PurchaseOrderGenerationResult> GenerateFromLowStockAsync(
        string? supplierFilter,
        string? operatorUsername,
        CancellationToken cancellationToken = default)
    {
        await _alertService.ScanAsync(cancellationToken).ConfigureAwait(false);
        var openAlerts = await _alertRepository.GetOpenAsync(cancellationToken).ConfigureAwait(false);
        var lowAlerts = openAlerts
            .Where(a => a.AlertType is InventoryAlertTypes.LowStock or InventoryAlertTypes.Stockout)
            .ToList();

        if (lowAlerts.Count == 0)
        {
            return new PurchaseOrderGenerationResult
            {
                Success = false,
                Message = "No low-stock or stockout items to order.",
                GeneratedPoSummary = "No purchase orders generated."
            };
        }

        var products = (await _inventoryRepository.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(p => p.ProductCode, StringComparer.OrdinalIgnoreCase);
        var velocity = await _alertService.GetAverageDailySalesAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var suppliers = (await _supplierRepository.GetAllAsync(activeOnly: false, cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(s => s.SupplierCode, StringComparer.OrdinalIgnoreCase);

        var candidates = new List<(LocalInventoryItem Item, decimal AvgDaily)>();
        foreach (var alert in lowAlerts)
        {
            if (!products.TryGetValue(alert.ProductCode, out var item))
            {
                continue;
            }

            velocity.TryGetValue(item.ProductCode, out var avg);
            candidates.Add((item, avg));
        }

        if (!string.IsNullOrWhiteSpace(supplierFilter)
            && !supplierFilter.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            candidates = candidates
                .Where(c => ResolveSupplierCode(c.Item)
                    .Equals(supplierFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var grouped = candidates
            .GroupBy(c => ResolveSupplierCode(c.Item), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (grouped.Count == 0)
        {
            return new PurchaseOrderGenerationResult
            {
                Success = false,
                Message = "No matching products for the selected supplier filter.",
                GeneratedPoSummary = "No purchase orders generated."
            };
        }

        var created = new List<PurchaseOrder>();
        var summaryParts = new List<string>();

        foreach (var group in grouped)
        {
            var supplierCode = group.Key;
            var supplierName = ResolveSupplierName(group.First().Item, suppliers);
            var lines = new List<PurchaseOrderLine>();

            foreach (var (item, avgDaily) in group
                         .GroupBy(g => g.Item.ProductCode, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First()))
            {
                var qty = CalculateSuggestedQty(item, avgDaily);
                if (qty <= 0)
                {
                    continue;
                }

                var unitCost = item.UnitPrice;
                lines.Add(new PurchaseOrderLine
                {
                    ProductCode = item.ProductCode,
                    ProductName = item.Name,
                    CurrentStock = item.StockQuantity,
                    MinReorderQty = _alertService.ResolveMinReorder(item),
                    MaxStockCapacity = _alertService.ResolveMaxCapacity(item),
                    AverageDailySales = PosTaxCalculator.RoundMoney(avgDaily),
                    SuggestedQty = qty,
                    UnitCost = unitCost,
                    LineTotal = PosTaxCalculator.RoundMoney(qty * unitCost)
                });
            }

            if (lines.Count == 0)
            {
                continue;
            }

            var poNumber = $"PO-{DateTime.UtcNow:yyyyMMddHHmmss}-{supplierCode}";
            var totalQty = lines.Sum(l => l.SuggestedQty);
            var totalCost = lines.Sum(l => l.LineTotal);
            var summary =
                $"Supplier {supplierName} ({supplierCode}): {lines.Count} line(s), qty {totalQty:N2}, est. {totalCost:N2} MWK.";

            var order = new PurchaseOrder
            {
                PoNumber = poNumber.Length > 80 ? poNumber[..80] : poNumber,
                SupplierCode = supplierCode,
                SupplierName = supplierName,
                Status = PurchaseOrderStatuses.ReadyForSignOff,
                LineCount = lines.Count,
                TotalQuantity = PosTaxCalculator.RoundMoney(totalQty),
                TotalEstimatedCost = PosTaxCalculator.RoundMoney(totalCost),
                OperatorUsername = operatorUsername,
                Notes = "Auto-generated from low-stock / stockout alerts.",
                SummaryText = summary
            };

            var poId = await _purchaseOrderRepository.CreateAsync(order, lines, cancellationToken)
                .ConfigureAwait(false);
            order.PoId = poId;
            created.Add(order);
            summaryParts.Add($"#{poId} {summary}");
        }

        if (created.Count == 0)
        {
            return new PurchaseOrderGenerationResult
            {
                Success = false,
                Message = "Suggested restock quantities were zero after capacity checks.",
                GeneratedPoSummary = "No purchase orders generated."
            };
        }

        var fullSummary = string.Join(Environment.NewLine, summaryParts);
        _logger.LogInformation("Generated {Count} purchase order(s).", created.Count);

        return new PurchaseOrderGenerationResult
        {
            Success = true,
            Message = $"Generated {created.Count} purchase order(s).",
            GeneratedPoSummary = fullSummary,
            Orders = created
        };
    }

    public Task<IReadOnlyList<PurchaseOrder>> GetRecentOrdersAsync(CancellationToken cancellationToken = default) =>
        _purchaseOrderRepository.GetRecentAsync(50, cancellationToken);

    public Task<IReadOnlyList<PurchaseOrderLine>> GetLinesAsync(long poId, CancellationToken cancellationToken = default) =>
        _purchaseOrderRepository.GetLinesAsync(poId, cancellationToken);

    public async Task<string> ExportCsvAsync(long poId, CancellationToken cancellationToken = default)
    {
        var order = await _purchaseOrderRepository.GetByIdAsync(poId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Purchase order {poId} was not found.");
        var lines = await _purchaseOrderRepository.GetLinesAsync(poId, cancellationToken).ConfigureAwait(false);

        var path = PromptSavePath($"PO_{order.PoNumber}.csv", "CSV|*.csv");
        if (path is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("PoNumber,SupplierCode,SupplierName,Status,GeneratedAtUtc,ProductCode,ProductName,CurrentStock,MinReorderQty,AvgDailySales,SuggestedQty,UnitCost,LineTotal");
        foreach (var line in lines)
        {
            sb.AppendLine(
                $"{Escape(order.PoNumber)},{Escape(order.SupplierCode)},{Escape(order.SupplierName)},{Escape(order.Status)},{order.GeneratedAtUtc:O},{Escape(line.ProductCode)},{Escape(line.ProductName)},{line.CurrentStock:0.00},{line.MinReorderQty:0.00},{line.AverageDailySales:0.00},{line.SuggestedQty:0.00},{line.UnitCost:0.00},{line.LineTotal:0.00}");
        }

        sb.AppendLine();
        sb.AppendLine($"TOTAL,,,,,{order.LineCount},,,,{order.TotalQuantity:0.00},,{order.TotalEstimatedCost:0.00}");
        await File.WriteAllTextAsync(path, sb.ToString(), Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        await _purchaseOrderRepository.MarkExportedAsync(poId, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task ExportPdfAsync(long poId, CancellationToken cancellationToken = default)
    {
        var order = await _purchaseOrderRepository.GetByIdAsync(poId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Purchase order {poId} was not found.");
        var lines = await _purchaseOrderRepository.GetLinesAsync(poId, cancellationToken).ConfigureAwait(false);

        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            PagePadding = new Thickness(40)
        };
        doc.Blocks.Add(new Paragraph(new Run("Purchase Order — Albert Retail Terminal"))
        {
            FontSize = 18,
            FontWeight = FontWeights.Bold
        });
        doc.Blocks.Add(Line($"PO: {order.PoNumber}"));
        doc.Blocks.Add(Line($"Supplier: {order.SupplierName} ({order.SupplierCode})"));
        doc.Blocks.Add(Line($"Status: {order.Status}"));
        doc.Blocks.Add(Line($"Generated (UTC): {order.GeneratedAtUtc:u}"));
        doc.Blocks.Add(Line($"Operator: {order.OperatorUsername}"));
        doc.Blocks.Add(new Paragraph());

        foreach (var line in lines)
        {
            doc.Blocks.Add(Line(
                $"{line.ProductCode} | {line.ProductName} | on-hand {line.CurrentStock:N2} | order {line.SuggestedQty:N2} @ {line.UnitCost:N2} = {line.LineTotal:N2} MWK"));
        }

        doc.Blocks.Add(new Paragraph());
        doc.Blocks.Add(Line($"Lines: {order.LineCount}"));
        doc.Blocks.Add(Line($"Total qty: {order.TotalQuantity:N2}"));
        doc.Blocks.Add(new Paragraph(new Run($"Estimated cost: {order.TotalEstimatedCost:N2} MWK"))
        {
            FontWeight = FontWeights.Bold
        });
        if (!string.IsNullOrWhiteSpace(order.SummaryText))
        {
            doc.Blocks.Add(Line(order.SummaryText));
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() == true)
            {
                PrintPageSizeGuard.ApplySafePageSize(
                    doc,
                    dialog,
                    fallbackWidthDip: 793,
                    fallbackHeightDip: 1122);
                PrintPageSizeGuard.EnsureDocumentReadyToPrint(doc);
                dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, order.PoNumber);
            }
        });

        await _purchaseOrderRepository.MarkExportedAsync(poId, cancellationToken).ConfigureAwait(false);
    }

    public decimal CalculateSuggestedQty(LocalInventoryItem item, decimal averageDailySales)
    {
        var minReorder = _alertService.ResolveMinReorder(item);
        var maxCapacity = _alertService.ResolveMaxCapacity(item);
        var velocityTarget = PosTaxCalculator.RoundMoney(averageDailySales * _options.TargetDaysOfCover);
        var floorTarget = Math.Max(minReorder * 2m, velocityTarget);
        if (floorTarget <= 0)
        {
            floorTarget = minReorder > 0 ? minReorder : _options.DefaultMinReorderQty;
        }

        var needed = PosTaxCalculator.RoundMoney(Math.Max(0m, floorTarget - item.StockQuantity));
        if (maxCapacity > 0)
        {
            var room = Math.Max(0m, maxCapacity - item.StockQuantity);
            needed = Math.Min(needed, room);
        }

        return PosTaxCalculator.RoundMoney(needed);
    }

    private string ResolveSupplierCode(LocalInventoryItem item) =>
        string.IsNullOrWhiteSpace(item.SupplierCode) ? _options.DefaultSupplierCode : item.SupplierCode.Trim();

    private string ResolveSupplierName(
        LocalInventoryItem item,
        IReadOnlyDictionary<string, InventorySupplier> suppliers)
    {
        var code = ResolveSupplierCode(item);
        if (!string.IsNullOrWhiteSpace(item.SupplierName))
        {
            return item.SupplierName!;
        }

        if (suppliers.TryGetValue(code, out var supplier))
        {
            return supplier.SupplierName;
        }

        return code.Equals(_options.DefaultSupplierCode, StringComparison.OrdinalIgnoreCase)
            ? _options.DefaultSupplierName
            : code;
    }

    private static string? PromptSavePath(string fileName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = fileName,
            Filter = filter,
            AddExtension = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static Paragraph Line(string text) => new(new Run(text));

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var needsQuotes = value.Contains(',') || value.Contains('"') || value.Contains('\n');
        var escaped = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{escaped}\"" : escaped;
    }
}
