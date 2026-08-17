using System.Windows.Media.Imaging;

namespace PointOfSale.App.Services;

/// <summary>
/// Structured thermal receipt layout produced by <see cref="MraReceiptLayoutService"/>.
/// </summary>
public sealed class MraReceiptLayoutViewModel
{
    public required IReadOnlyList<string> HeaderLines { get; init; }
    public required IReadOnlyList<string> MetaLines { get; init; }
    public required IReadOnlyList<MraReceiptLineItemViewModel> LineItems { get; init; }
    public required IReadOnlyList<string> TaxBreakdownLines { get; init; }
    public required IReadOnlyList<string> TotalsLines { get; init; }
    public required MraFiscalStatusBlockViewModel FiscalStatus { get; init; }
    public required IReadOnlyList<string> FooterLines { get; init; }

    /// <summary>Monospace text lines in print order (excluding the QR image block).</summary>
    public required IReadOnlyList<string> OrderedTextLines { get; init; }

    public int CharactersPerLine { get; init; }
}

public sealed class MraReceiptLineItemViewModel
{
    public required string Description { get; init; }
    public required string QuantityPriceLine { get; init; }

    /// <summary>Optional <c>DISCOUNT … -1,000.00</c> line when a discount was applied.</summary>
    public string DiscountLine { get; init; } = string.Empty;

    public required string VatBreakdownLine { get; init; }
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal LineTotal { get; init; }
    public decimal LineDiscount { get; init; }
    public decimal LineVat { get; init; }
    public string TaxRateId { get; init; } = "A";
}

public sealed class MraFiscalStatusBlockViewModel
{
    public required string Title { get; init; }
    public required IReadOnlyList<string> BodyLines { get; init; }
    public bool IsOfflinePending { get; init; }
    public bool IncludeQrCode { get; init; }
    public string FiscalSignature { get; init; } = string.Empty;
    public string? VerificationUrl { get; init; }

    /// <summary>QRCoder module matrix (true = dark). Null when offline or no verification URL.</summary>
    public bool[,]? QrModuleMatrix { get; init; }

    /// <summary>WPF-ready QR bitmap rendered from the QRCoder matrix / PNG encoder.</summary>
    public BitmapSource? QrCodeImage { get; init; }
}
