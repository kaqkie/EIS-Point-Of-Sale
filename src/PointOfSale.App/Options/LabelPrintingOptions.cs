namespace PointOfSale.App.Options;

/// <summary>
/// Product label / shelf-edge tag printing defaults (thermal ESC/POS + raster preview).
/// </summary>
public sealed class LabelPrintingOptions
{
    public const string SectionName = "LabelPrinting";

    /// <summary>Default template key: ShelfEdge50x30 | ShelfEdge40x25 | StickyBarcode | FiscalQrTag.</summary>
    public string DefaultTemplateType { get; set; } = LabelTemplateTypes.ShelfEdge50x30;

    /// <summary>Default copies per selected SKU when building a batch.</summary>
    public int DefaultQuantityPerItem { get; set; } = 1;

    /// <summary>
    /// Base URL for MRA fiscal verification QR payloads.
    /// Invoice id is appended as a query parameter when generating fiscal QR labels.
    /// </summary>
    public string MraVerificationBaseUrl { get; set; } = "https://dev-eis-portal.mra.mw/verify";

    /// <summary>Include "incl. 17.5% VAT" on shelf-edge price lines.</summary>
    public bool ShowVatInclusivePrice { get; set; } = true;

    /// <summary>Raster barcode module height (pixels) for WPF previews.</summary>
    public int PreviewBarcodeHeightPx { get; set; } = 72;

    /// <summary>Raster barcode width (pixels) for WPF previews.</summary>
    public int PreviewBarcodeWidthPx { get; set; } = 280;
}

public static class LabelTemplateTypes
{
    public const string ShelfEdge50x30 = "ShelfEdge50x30";
    public const string ShelfEdge40x25 = "ShelfEdge40x25";
    public const string StickyBarcode = "StickyBarcode";
    public const string FiscalQrTag = "FiscalQrTag";

    public static readonly string[] All =
    [
        ShelfEdge50x30,
        ShelfEdge40x25,
        StickyBarcode,
        FiscalQrTag
    ];
}
