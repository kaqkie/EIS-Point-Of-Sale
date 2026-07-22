namespace PointOfSale.Core.Entities;

public static class BarcodeSymbologies
{
    public const string Ean13 = "EAN-13";
    public const string Code128 = "Code128";
    public const string QrCode = "QR";

    public static readonly string[] ProductLabelSymbologies = [Ean13, Code128];
}

public static class LabelBatchStatuses
{
    public const string Draft = "Draft";
    public const string Printed = "Printed";
    public const string Failed = "Failed";
}

/// <summary>Persisted shelf-edge / barcode label batch for reprint and audit.</summary>
public sealed class LabelPrintBatch
{
    public long BatchId { get; set; }
    public string TemplateType { get; set; } = string.Empty;
    public int QuantityPerItem { get; set; }
    public int ProductCount { get; set; }
    public int LabelCount { get; set; }
    public string Status { get; set; } = LabelBatchStatuses.Draft;
    public string? OperatorUsername { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PrintedAtUtc { get; set; }
}

public sealed class LabelPrintBatchLine
{
    public long BatchLineId { get; set; }
    public long BatchId { get; set; }
    public string ProductCode { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPriceNet { get; set; }
    public decimal UnitPriceGross { get; set; }
    public int Quantity { get; set; }
    public string Symbology { get; set; } = BarcodeSymbologies.Code128;
}

/// <summary>In-memory content for a single printable product label.</summary>
public sealed class ProductLabelContent
{
    public required string ProductCode { get; init; }
    public required string ProductName { get; init; }
    public decimal UnitPriceNet { get; init; }
    public decimal VatAmount { get; init; }
    public decimal UnitPriceGross { get; init; }
    public decimal VatRatePercent { get; init; }
    public string Symbology { get; init; } = BarcodeSymbologies.Code128;
    public string BarcodePayload { get; init; } = string.Empty;
    public string? QrPayload { get; init; }
    public bool ShowVatInclusive { get; init; } = true;
}
