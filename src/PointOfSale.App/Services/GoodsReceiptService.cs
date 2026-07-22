using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface IGoodsReceiptService
{
    Task<IReadOnlyList<PurchaseOrder>> GetReceivablePurchaseOrdersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PurchaseOrderLine>> GetPurchaseOrderLinesAsync(long poId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GoodsReceiptNote>> GetRecentGrnsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GoodsReceiptLine>> GetGrnLinesAsync(long grnId, CancellationToken cancellationToken = default);
    GoodsReceiptDraft CreateDraftFromPurchaseOrder(PurchaseOrder order, IReadOnlyList<PurchaseOrderLine> poLines);
    bool TryScanBarcode(GoodsReceiptDraft draft, string barcode, decimal quantity = 1m);
    decimal CalculateWeightedAverageCost(decimal previousStock, decimal previousAvgCost, decimal receiveQty, decimal unitCost);
    decimal CalculateRetailPrice(decimal averageUnitCost, decimal markupPercent);
    Task<GoodsReceiptPostResult> SaveDraftAsync(GoodsReceiptDraft draft, CancellationToken cancellationToken = default);
    Task<GoodsReceiptPostResult> PostAsync(GoodsReceiptDraft draft, CancellationToken cancellationToken = default);
}

public sealed class GoodsReceiptDraft
{
    public long? GrnId { get; set; }
    public string GrnNumber { get; set; } = string.Empty;
    public long PoId { get; set; }
    public string PoNumber { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string? DeliveryNoteNumber { get; set; }
    public string? SupplierInvoiceNumber { get; set; }
    public string? OperatorUsername { get; set; }
    public string? Notes { get; set; }
    public List<GoodsReceiptLine> Lines { get; set; } = [];
}

public sealed class GoodsReceiptPostResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public long GrnId { get; init; }
    public string GrnNumber { get; init; } = string.Empty;

    public static GoodsReceiptPostResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>
/// GRN engine: receive against Phase 20 POs, replenish LocalInventory, recalculate WAC and retail markup (MWK).
/// </summary>
public sealed class GoodsReceiptService : IGoodsReceiptService
{
    private readonly IPurchaseOrderRepository _purchaseOrderRepository;
    private readonly IGoodsReceiptRepository _grnRepository;
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly GoodsReceiptOptions _options;
    private readonly ILogger<GoodsReceiptService> _logger;

    public GoodsReceiptService(
        IPurchaseOrderRepository purchaseOrderRepository,
        IGoodsReceiptRepository grnRepository,
        ILocalInventoryRepository inventoryRepository,
        IOptions<GoodsReceiptOptions> options,
        ILogger<GoodsReceiptService> logger)
    {
        _purchaseOrderRepository = purchaseOrderRepository;
        _grnRepository = grnRepository;
        _inventoryRepository = inventoryRepository;
        _options = options.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<PurchaseOrder>> GetReceivablePurchaseOrdersAsync(
        CancellationToken cancellationToken = default) =>
        _purchaseOrderRepository.GetReceivableAsync(cancellationToken);

    public Task<IReadOnlyList<PurchaseOrderLine>> GetPurchaseOrderLinesAsync(
        long poId,
        CancellationToken cancellationToken = default) =>
        _purchaseOrderRepository.GetLinesAsync(poId, cancellationToken);

    public Task<IReadOnlyList<GoodsReceiptNote>> GetRecentGrnsAsync(CancellationToken cancellationToken = default) =>
        _grnRepository.GetRecentAsync(50, cancellationToken);

    public Task<IReadOnlyList<GoodsReceiptLine>> GetGrnLinesAsync(long grnId, CancellationToken cancellationToken = default) =>
        _grnRepository.GetLinesAsync(grnId, cancellationToken);

    public GoodsReceiptDraft CreateDraftFromPurchaseOrder(PurchaseOrder order, IReadOnlyList<PurchaseOrderLine> poLines)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(poLines);

        return new GoodsReceiptDraft
        {
            GrnNumber = $"GRN-{DateTime.UtcNow:yyyyMMddHHmmss}-{order.PoId}",
            PoId = order.PoId,
            PoNumber = order.PoNumber,
            SupplierCode = order.SupplierCode,
            SupplierName = order.SupplierName,
            Lines = poLines.Select(l => new GoodsReceiptLine
            {
                ProductCode = l.ProductCode,
                ProductName = l.ProductName,
                OrderedQty = l.SuggestedQty,
                ReceivedQty = 0m,
                DamagedQty = 0m,
                UnitCost = l.UnitCost
            }).ToList()
        };
    }

    public bool TryScanBarcode(GoodsReceiptDraft draft, string barcode, decimal quantity = 1m)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(barcode))
        {
            return false;
        }

        quantity = Math.Max(0.01m, quantity);
        var code = barcode.Trim();
        var line = draft.Lines.FirstOrDefault(l =>
            l.ProductCode.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return false;
        }

        line.ReceivedQty = PosTaxCalculator.RoundMoney(line.ReceivedQty + quantity);
        return true;
    }

    public decimal CalculateWeightedAverageCost(
        decimal previousStock,
        decimal previousAvgCost,
        decimal receiveQty,
        decimal unitCost)
    {
        receiveQty = Math.Max(0m, receiveQty);
        if (receiveQty <= 0)
        {
            return PosTaxCalculator.RoundMoney(previousAvgCost > 0 ? previousAvgCost : unitCost);
        }

        var priorCost = previousAvgCost > 0 ? previousAvgCost : unitCost;
        var priorStock = Math.Max(0m, previousStock);
        var totalQty = priorStock + receiveQty;
        if (totalQty <= 0)
        {
            return PosTaxCalculator.RoundMoney(unitCost);
        }

        var wac = ((priorStock * priorCost) + (receiveQty * unitCost)) / totalQty;
        return PosTaxCalculator.RoundMoney(wac);
    }

    public decimal CalculateRetailPrice(decimal averageUnitCost, decimal markupPercent)
    {
        var markup = markupPercent > 0 ? markupPercent : _options.DefaultMarkupPercent;
        return PosTaxCalculator.RoundMoney(averageUnitCost * (1m + (markup / 100m)));
    }

    public async Task<GoodsReceiptPostResult> SaveDraftAsync(
        GoodsReceiptDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Lines.Count == 0)
        {
            return GoodsReceiptPostResult.Fail("GRN has no lines.");
        }

        var note = ToNote(draft, GoodsReceiptStatuses.Draft);
        if (draft.GrnId is long existingId)
        {
            note.GrnId = existingId;
            await _grnRepository.UpdateDraftAsync(note, draft.Lines, cancellationToken).ConfigureAwait(false);
            return new GoodsReceiptPostResult
            {
                Success = true,
                Message = $"Draft GRN {note.GrnNumber} saved.",
                GrnId = existingId,
                GrnNumber = note.GrnNumber
            };
        }

        var id = await _grnRepository.CreateAsync(note, draft.Lines, cancellationToken).ConfigureAwait(false);
        draft.GrnId = id;
        return new GoodsReceiptPostResult
        {
            Success = true,
            Message = $"Draft GRN {note.GrnNumber} created.",
            GrnId = id,
            GrnNumber = note.GrnNumber
        };
    }

    public async Task<GoodsReceiptPostResult> PostAsync(
        GoodsReceiptDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Lines.All(l => l.ReceivedQty <= 0 && l.DamagedQty <= 0))
        {
            return GoodsReceiptPostResult.Fail("Enter received and/or damaged quantities before posting.");
        }

        // Persist draft first so we have a GrnId, then apply inventory and mark posted.
        var save = await SaveDraftAsync(draft, cancellationToken).ConfigureAwait(false);
        if (!save.Success || draft.GrnId is null)
        {
            return save;
        }

        var postedLines = new List<GoodsReceiptLine>();
        foreach (var line in draft.Lines)
        {
            var goodQty = Math.Max(0m, line.ReceivedQty - Math.Max(0m, line.DamagedQty));
            if (line.ReceivedQty <= 0 && line.DamagedQty <= 0)
            {
                postedLines.Add(line);
                continue;
            }

            var item = await _inventoryRepository.GetByProductCodeAsync(line.ProductCode, cancellationToken)
                .ConfigureAwait(false);
            if (item is null)
            {
                return GoodsReceiptPostResult.Fail($"Product '{line.ProductCode}' is missing from local inventory.");
            }

            var previousAvg = item.AverageUnitCost > 0 ? item.AverageUnitCost : item.UnitPrice;
            var newAvg = CalculateWeightedAverageCost(item.StockQuantity, previousAvg, goodQty, line.UnitCost);
            var markup = item.MarkupPercent > 0 ? item.MarkupPercent : _options.DefaultMarkupPercent;
            decimal? newRetail = null;
            if (_options.ApplyRetailMarkupOnReceipt && goodQty > 0)
            {
                newRetail = CalculateRetailPrice(newAvg, markup);
            }

            line.PreviousStock = item.StockQuantity;
            line.PreviousAvgCost = previousAvg;
            line.PreviousRetailPrice = item.UnitPrice;
            line.NewAvgCost = newAvg;
            line.NewRetailPrice = newRetail ?? item.UnitPrice;
            line.NewStock = PosTaxCalculator.RoundMoney(item.StockQuantity + goodQty);

            if (goodQty > 0)
            {
                await _inventoryRepository.ApplyGoodsReceiptAsync(
                        line.ProductCode,
                        goodQty,
                        line.UnitCost,
                        newAvg,
                        newRetail,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            postedLines.Add(line);
        }

        draft.Lines = postedLines;
        var note = ToNote(draft, GoodsReceiptStatuses.Draft);
        note.GrnId = draft.GrnId.Value;
        await _grnRepository.UpdateDraftAsync(note, draft.Lines, cancellationToken).ConfigureAwait(false);
        await _grnRepository.MarkPostedAsync(draft.GrnId.Value, cancellationToken).ConfigureAwait(false);

        var ordered = draft.Lines.Sum(l => l.OrderedQty);
        var received = draft.Lines.Sum(l => l.ReceivedQty);
        var poStatus = received + 0.0001m >= ordered
            ? PurchaseOrderStatuses.Received
            : PurchaseOrderStatuses.PartiallyReceived;
        await _purchaseOrderRepository.UpdateStatusAsync(draft.PoId, poStatus, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Posted GRN {GrnNumber} for PO {PoNumber}; stock replenished for {Lines} line(s).",
            draft.GrnNumber,
            draft.PoNumber,
            draft.Lines.Count);

        return new GoodsReceiptPostResult
        {
            Success = true,
            Message = $"Posted {draft.GrnNumber}. PO marked {poStatus}.",
            GrnId = draft.GrnId.Value,
            GrnNumber = draft.GrnNumber
        };
    }

    private static GoodsReceiptNote ToNote(GoodsReceiptDraft draft, string status) =>
        new()
        {
            GrnId = draft.GrnId ?? 0,
            GrnNumber = string.IsNullOrWhiteSpace(draft.GrnNumber)
                ? $"GRN-{DateTime.UtcNow:yyyyMMddHHmmss}"
                : draft.GrnNumber,
            PoId = draft.PoId,
            PoNumber = draft.PoNumber,
            SupplierCode = draft.SupplierCode,
            SupplierName = draft.SupplierName,
            Status = status,
            DeliveryNoteNumber = draft.DeliveryNoteNumber,
            SupplierInvoiceNumber = draft.SupplierInvoiceNumber,
            OperatorUsername = draft.OperatorUsername,
            Notes = draft.Notes
        };
}
