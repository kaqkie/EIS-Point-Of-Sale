using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface ISupplierInvoiceReconciliationService
{
    Task<IReadOnlyList<GoodsReceiptNote>> GetPostedGrnsAsync(CancellationToken cancellationToken = default);
    Task<SupplierReconciliationResult> ReconcileAsync(
        long grnId,
        string supplierInvoiceNumber,
        DateTime? invoiceDate,
        decimal invoiceTotalMwk,
        IReadOnlyDictionary<string, decimal>? invoiceQuantitiesByProduct,
        IReadOnlyDictionary<string, decimal>? invoiceUnitCostsByProduct,
        string? operatorUsername,
        string? discrepancyNotes,
        CancellationToken cancellationToken = default);
    Task SignOffAsync(long reconciliationId, string? notes, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupplierInvoiceReconciliation>> GetRecentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SupplierInvoiceReconciliationLine>> GetLinesAsync(
        long reconciliationId,
        CancellationToken cancellationToken = default);
}

public sealed class SupplierReconciliationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string DiscrepancyNotes { get; init; } = string.Empty;
    public long ReconciliationId { get; init; }
    public string Status { get; init; } = string.Empty;
    public IReadOnlyList<SupplierInvoiceReconciliationLine> DiscrepancyLines { get; init; } =
        Array.Empty<SupplierInvoiceReconciliationLine>();

    public static SupplierReconciliationResult Fail(string message) =>
        new() { Success = false, Message = message };
}

/// <summary>
/// Cross-references GRN received quantities against supplier delivery notes / tax invoices
/// and flags short deliveries, damage, and cost/total variances before AP sign-off.
/// </summary>
public sealed class SupplierInvoiceReconciliationService : ISupplierInvoiceReconciliationService
{
    private readonly IGoodsReceiptRepository _grnRepository;
    private readonly ISupplierInvoiceReconciliationRepository _reconciliationRepository;
    private readonly GoodsReceiptOptions _options;
    private readonly ILogger<SupplierInvoiceReconciliationService> _logger;

    public SupplierInvoiceReconciliationService(
        IGoodsReceiptRepository grnRepository,
        ISupplierInvoiceReconciliationRepository reconciliationRepository,
        IOptions<GoodsReceiptOptions> options,
        ILogger<SupplierInvoiceReconciliationService> logger)
    {
        _grnRepository = grnRepository;
        _reconciliationRepository = reconciliationRepository;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GoodsReceiptNote>> GetPostedGrnsAsync(CancellationToken cancellationToken = default)
    {
        var recent = await _grnRepository.GetRecentAsync(100, cancellationToken).ConfigureAwait(false);
        return recent.Where(g => g.Status == GoodsReceiptStatuses.Posted).ToList();
    }

    public async Task<SupplierReconciliationResult> ReconcileAsync(
        long grnId,
        string supplierInvoiceNumber,
        DateTime? invoiceDate,
        decimal invoiceTotalMwk,
        IReadOnlyDictionary<string, decimal>? invoiceQuantitiesByProduct,
        IReadOnlyDictionary<string, decimal>? invoiceUnitCostsByProduct,
        string? operatorUsername,
        string? discrepancyNotes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(supplierInvoiceNumber))
        {
            return SupplierReconciliationResult.Fail("Supplier invoice number is required.");
        }

        var grn = await _grnRepository.GetByIdAsync(grnId, cancellationToken).ConfigureAwait(false);
        if (grn is null || grn.Status != GoodsReceiptStatuses.Posted)
        {
            return SupplierReconciliationResult.Fail("Select a posted GRN to reconcile.");
        }

        var lines = await _grnRepository.GetLinesAsync(grnId, cancellationToken).ConfigureAwait(false);
        if (lines.Count == 0)
        {
            return SupplierReconciliationResult.Fail("GRN has no lines.");
        }

        invoiceQuantitiesByProduct ??= new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        invoiceUnitCostsByProduct ??= new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

        var discrepancies = new List<SupplierInvoiceReconciliationLine>();
        var receivedTotal = 0m;
        var noteParts = new List<string>();

        foreach (var line in lines)
        {
            var goodQty = Math.Max(0m, line.ReceivedQty - Math.Max(0m, line.DamagedQty));
            receivedTotal += PosTaxCalculator.RoundMoney(goodQty * line.UnitCost);

            var invoiceQty = invoiceQuantitiesByProduct.TryGetValue(line.ProductCode, out var iq)
                ? iq
                : line.ReceivedQty;
            var invoiceUnitCost = invoiceUnitCostsByProduct.TryGetValue(line.ProductCode, out var ic)
                ? ic
                : line.UnitCost;

            var qtyDelta = line.OrderedQty - line.ReceivedQty;
            if (qtyDelta > _options.QuantityVarianceTolerance)
            {
                discrepancies.Add(MakeLine(
                    line,
                    SupplierDiscrepancyTypes.ShortDelivery,
                    invoiceQty,
                    invoiceUnitCost,
                    $"Short delivery: ordered {line.OrderedQty:N2}, received {line.ReceivedQty:N2}."));
            }
            else if (-qtyDelta > _options.QuantityVarianceTolerance)
            {
                discrepancies.Add(MakeLine(
                    line,
                    SupplierDiscrepancyTypes.OverDelivery,
                    invoiceQty,
                    invoiceUnitCost,
                    $"Over delivery: ordered {line.OrderedQty:N2}, received {line.ReceivedQty:N2}."));
            }

            if (line.DamagedQty > _options.QuantityVarianceTolerance)
            {
                discrepancies.Add(MakeLine(
                    line,
                    SupplierDiscrepancyTypes.DamagedStock,
                    invoiceQty,
                    invoiceUnitCost,
                    $"Damaged stock reported: {line.DamagedQty:N2}."));
            }

            if (Math.Abs(invoiceUnitCost - line.UnitCost) > _options.InvoiceVarianceToleranceMwk)
            {
                discrepancies.Add(MakeLine(
                    line,
                    SupplierDiscrepancyTypes.CostVariance,
                    invoiceQty,
                    invoiceUnitCost,
                    $"Unit cost variance: GRN {line.UnitCost:N2} vs invoice {invoiceUnitCost:N2} MWK."));
            }

            if (Math.Abs(invoiceQty - line.ReceivedQty) > _options.QuantityVarianceTolerance)
            {
                discrepancies.Add(MakeLine(
                    line,
                    Math.Abs(invoiceQty) < Math.Abs(line.ReceivedQty)
                        ? SupplierDiscrepancyTypes.ShortDelivery
                        : SupplierDiscrepancyTypes.OverDelivery,
                    invoiceQty,
                    invoiceUnitCost,
                    $"Invoice qty {invoiceQty:N2} differs from received {line.ReceivedQty:N2}."));
            }
        }

        receivedTotal = PosTaxCalculator.RoundMoney(receivedTotal);
        invoiceTotalMwk = PosTaxCalculator.RoundMoney(invoiceTotalMwk);
        var variance = PosTaxCalculator.RoundMoney(invoiceTotalMwk - receivedTotal);
        if (Math.Abs(variance) > _options.InvoiceVarianceToleranceMwk)
        {
            discrepancies.Add(new SupplierInvoiceReconciliationLine
            {
                ProductCode = "*",
                ProductName = "Invoice total",
                DiscrepancyType = SupplierDiscrepancyTypes.InvoiceTotalVariance,
                OrderedQty = 0,
                ReceivedQty = 0,
                DamagedQty = 0,
                InvoiceQty = 0,
                UnitCost = receivedTotal,
                InvoiceUnitCost = invoiceTotalMwk,
                Message =
                    $"Invoice total {invoiceTotalMwk:N2} MWK vs received value {receivedTotal:N2} MWK (variance {variance:N2})."
            });
        }

        var status = discrepancies.Count == 0
            ? SupplierReconciliationStatuses.Matched
            : SupplierReconciliationStatuses.Discrepancy;

        foreach (var d in discrepancies)
        {
            noteParts.Add($"{d.DiscrepancyType}: {d.Message}");
        }

        var combinedNotes = string.IsNullOrWhiteSpace(discrepancyNotes)
            ? string.Join(" | ", noteParts)
            : $"{discrepancyNotes.Trim()} | {string.Join(" | ", noteParts)}";
        if (combinedNotes.Length > 1000)
        {
            combinedNotes = combinedNotes[..1000];
        }

        var header = new SupplierInvoiceReconciliation
        {
            GrnId = grn.GrnId,
            GrnNumber = grn.GrnNumber,
            SupplierInvoiceNumber = supplierInvoiceNumber.Trim(),
            InvoiceDate = invoiceDate,
            InvoiceTotalMwk = invoiceTotalMwk,
            ReceivedTotalMwk = receivedTotal,
            VarianceMwk = variance,
            Status = status,
            DiscrepancyNotes = string.IsNullOrWhiteSpace(combinedNotes) ? null : combinedNotes,
            OperatorUsername = operatorUsername
        };

        var id = await _reconciliationRepository.CreateAsync(header, discrepancies, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Reconciled GRN {GrnNumber} against invoice {Invoice}: status {Status}, variance {Variance}.",
            grn.GrnNumber,
            supplierInvoiceNumber,
            status,
            variance);

        return new SupplierReconciliationResult
        {
            Success = true,
            Message = status == SupplierReconciliationStatuses.Matched
                ? "Invoice matched GRN — ready for AP sign-off."
                : $"Reconciliation flagged {discrepancies.Count} discrepancy(ies).",
            DiscrepancyNotes = header.DiscrepancyNotes ?? string.Empty,
            ReconciliationId = id,
            Status = status,
            DiscrepancyLines = discrepancies
        };
    }

    public Task SignOffAsync(long reconciliationId, string? notes, CancellationToken cancellationToken = default) =>
        _reconciliationRepository.SignOffAsync(reconciliationId, notes, cancellationToken);

    public Task<IReadOnlyList<SupplierInvoiceReconciliation>> GetRecentAsync(
        CancellationToken cancellationToken = default) =>
        _reconciliationRepository.GetRecentAsync(50, cancellationToken);

    public Task<IReadOnlyList<SupplierInvoiceReconciliationLine>> GetLinesAsync(
        long reconciliationId,
        CancellationToken cancellationToken = default) =>
        _reconciliationRepository.GetLinesAsync(reconciliationId, cancellationToken);

    private static SupplierInvoiceReconciliationLine MakeLine(
        GoodsReceiptLine line,
        string type,
        decimal invoiceQty,
        decimal invoiceUnitCost,
        string message) =>
        new()
        {
            ProductCode = line.ProductCode,
            ProductName = line.ProductName,
            DiscrepancyType = type,
            OrderedQty = line.OrderedQty,
            ReceivedQty = line.ReceivedQty,
            DamagedQty = line.DamagedQty,
            InvoiceQty = invoiceQty,
            UnitCost = line.UnitCost,
            InvoiceUnitCost = invoiceUnitCost,
            Message = message
        };
}
