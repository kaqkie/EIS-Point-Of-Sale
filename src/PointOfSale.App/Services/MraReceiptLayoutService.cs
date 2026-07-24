using System.Collections;
using System.IO;
using System.Windows.Media.Imaging;
using PointOfSale.Core.Pricing;
using PointOfSale.Mra.Contracts.Sales;
using QRCoder;

namespace PointOfSale.App.Services;

public interface IMraReceiptLayoutService
{
    MraReceiptLayoutViewModel Build(ReceiptPrintRequest request, int charactersPerLine = 42);
}

/// <summary>
/// Builds the official MRA EIS legal receipt layout (thermal / FlowDocument / ESC-POS).
/// </summary>
public sealed class MraReceiptLayoutService : IMraReceiptLayoutService
{
    public const string LegalReceiptStartBanner = "*** START OF LEGAL RECEIPT ***";
    public const string LegalReceiptEndBanner = "*** END OF LEGAL RECEIPT ***";
    public const string QrPlaceholderMarker = "[MRA FISCAL QR]";
    public const string VatRegisteredBanner = "**VAT REGISTERED**";

    public static string StatutoryVatPercentLabel =>
        $"{PosTaxCalculator.MalawiStandardVatRatePercent:0.0}%";

    /// <summary>
    /// Formats the seller TIN from active store/terminal configuration.
    /// Never prints the historical sandbox placeholder <c>1234567890</c>.
    /// </summary>
    public static string FormatSellerTin(string? sellerTin)
    {
        if (PosConfigurationService.IsPlaceholderTaxpayerTin(sellerTin)
            || string.IsNullOrWhiteSpace(sellerTin))
        {
            return "NOT CONFIGURED";
        }

        return sellerTin.Trim();
    }

    public MraReceiptLayoutViewModel Build(ReceiptPrintRequest request, int charactersPerLine = 42)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (charactersPerLine < 24)
        {
            charactersPerLine = 24;
        }

        var fiscal = request.FiscalResponse is null
            ? null
            : FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
                request.FiscalResponse,
                request.InvoiceNumber);
        var fiscalSignature = fiscal?.ResolveFiscalSignature() ?? string.Empty;
        var verificationUrl = fiscal?.VerificationUrl?.Trim() ?? string.Empty;
        var isOfflinePending = FiscalReceiptEnricher.IsOfflinePlaceholder(fiscalSignature);
        var includeQr = !isOfflinePending && !string.IsNullOrWhiteSpace(verificationUrl);

        bool[,]? qrMatrix = null;
        BitmapSource? qrImage = null;
        if (includeQr)
        {
            (qrMatrix, qrImage) = RenderQrCoderMatrix(verificationUrl);
        }

        var localTime = request.InvoiceDateTime.Kind == DateTimeKind.Utc
            ? request.InvoiceDateTime.ToLocalTime()
            : request.InvoiceDateTime;

        // ---- Header (START + MRA portal + vendor identity) ----
        var header = new List<string>
        {
            Center(LegalReceiptStartBanner, charactersPerLine),
            Center("MALAWI REVENUE AUTHORITY", charactersPerLine),
            Center("Electronic Invoicing System (EIS)", charactersPerLine),
            Center("MRA Portal — eis-portal.mra.mw", charactersPerLine),
            Separator('-', charactersPerLine),
            Center(Truncate(request.TradingName, charactersPerLine), charactersPerLine)
        };

        foreach (var address in request.AddressLines.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            header.Add(Center(Truncate(address.Trim(), charactersPerLine), charactersPerLine));
        }

        if (!string.IsNullOrWhiteSpace(request.ContactPhone))
        {
            header.Add(Center(Truncate($"Tel: {request.ContactPhone.Trim()}", charactersPerLine), charactersPerLine));
        }

        if (!string.IsNullOrWhiteSpace(request.ContactEmail))
        {
            header.Add(Center(Truncate($"Email: {request.ContactEmail.Trim()}", charactersPerLine), charactersPerLine));
        }

        header.Add(Center(Truncate($"TIN: {FormatSellerTin(request.SellerTin)}", charactersPerLine), charactersPerLine));
        header.Add(Center(VatRegisteredBanner, charactersPerLine));
        header.Add(Separator('-', charactersPerLine));

        // ---- Buyer + receipt metadata ----
        var buyerTin = string.IsNullOrWhiteSpace(request.BuyerTin) ? "N/A" : request.BuyerTin.Trim();
        var buyerName = string.IsNullOrWhiteSpace(request.BuyerName) ? "WALK-IN CUSTOMER" : request.BuyerName.Trim();
        var meta = new List<string>
        {
            Truncate($"Buyer's TIN: {buyerTin}", charactersPerLine),
            Truncate($"Buyer's Name: {buyerName}", charactersPerLine),
            Truncate($"RECEIPT NUMBER: {request.InvoiceNumber}", charactersPerLine),
            Truncate($"Date: {localTime:yyyy-MM-dd}", charactersPerLine),
            Truncate($"Time: {localTime:HH:mm:ss}", charactersPerLine),
            Separator('-', charactersPerLine),
            Columns("QTY DESCRIPTION", "TOTAL TAX", charactersPerLine)
        };

        // ---- Line items: qty + description | total + tax code ----
        var lineItems = new List<MraReceiptLineItemViewModel>();
        foreach (var item in request.LineItems)
        {
            var taxCode = string.IsNullOrWhiteSpace(item.TaxRateId) ? "A" : item.TaxRateId.Trim().ToUpperInvariant();
            var left = $"{item.Quantity:N2} {item.Description}";
            var right = $"{item.Total:N2} {taxCode}";
            var qtyPriceLine = Columns(left, right, charactersPerLine);
            // Keep a secondary line for unit detail (optional clarity on narrow paper).
            var detailLine = Columns(
                $"  @ {item.UnitPrice:N2}",
                taxCode,
                charactersPerLine);

            lineItems.Add(new MraReceiptLineItemViewModel
            {
                Description = Truncate(item.Description, charactersPerLine),
                QuantityPriceLine = qtyPriceLine,
                VatBreakdownLine = detailLine,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                LineTotal = item.Total,
                LineVat = item.TotalVat,
                TaxRateId = taxCode
            });
        }

        // ---- Tax breakdown: TAXABLE A-17.5% / VAT A=17.5% ----
        var taxLines = new List<string> { Separator('-', charactersPerLine) };
        if (request.TaxBreakdown.Count == 0)
        {
            taxLines.Add(Columns($"TAXABLE A-{StatutoryVatPercentLabel}", $"{request.ResolveSubtotalNet():N2}", charactersPerLine));
            taxLines.Add(Columns($"VAT A={StatutoryVatPercentLabel}", $"{request.ResolveTotalVat():N2}", charactersPerLine));
        }
        else
        {
            foreach (var tax in request.TaxBreakdown)
            {
                var rateId = string.IsNullOrWhiteSpace(tax.RateId) ? "A" : tax.RateId.Trim().ToUpperInvariant();
                var rateLabel = FormatStatutoryRateLabel(rateId);
                taxLines.Add(Columns($"TAXABLE {rateId}-{rateLabel}", $"{tax.TaxableAmount:N2}", charactersPerLine));
                taxLines.Add(Columns($"VAT {rateId}={rateLabel}", $"{tax.TaxAmount:N2}", charactersPerLine));
            }
        }

        // ---- Summary totals ----
        var totals = new List<string>
        {
            Separator('-', charactersPerLine),
            Columns("TOTAL VAT", $"{request.ResolveTotalVat():N2}", charactersPerLine),
            Columns("TOTAL", $"{request.InvoiceTotal:N2}", charactersPerLine),
            Columns("AMOUNT", $"{request.AmountTendered:N2}", charactersPerLine),
            Columns("CHANGE", $"{request.ChangeDue:N2}", charactersPerLine),
            Separator('-', charactersPerLine)
        };

        // ---- Fiscal block (signature / offline) — QR is rendered after this, before END ----
        var fiscalBody = new List<string>();
        if (isOfflinePending)
        {
            fiscalBody.Add(Center("MRA EIS: OFFLINE — queued for sync", charactersPerLine));
            foreach (var chunk in Chunk(
                         string.IsNullOrWhiteSpace(fiscalSignature)
                             ? FiscalReceiptEnricher.OfflinePendingPlaceholder
                             : fiscalSignature,
                         charactersPerLine))
            {
                fiscalBody.Add(chunk);
            }
        }
        else
        {
            fiscalBody.Add(Center("MRA EIS FISCAL SIGNATURE", charactersPerLine));
            foreach (var chunk in Chunk(fiscalSignature, charactersPerLine))
            {
                fiscalBody.Add(chunk);
            }

            if (!string.IsNullOrWhiteSpace(verificationUrl))
            {
                fiscalBody.Add(Center("Verification URL", charactersPerLine));
                foreach (var chunk in Chunk(verificationUrl, charactersPerLine))
                {
                    fiscalBody.Add(chunk);
                }
            }
        }

        var fiscalStatus = new MraFiscalStatusBlockViewModel
        {
            Title = "MRA EIS FISCAL",
            BodyLines = fiscalBody,
            IsOfflinePending = isOfflinePending,
            IncludeQrCode = includeQr,
            FiscalSignature = fiscalSignature,
            VerificationUrl = string.IsNullOrWhiteSpace(verificationUrl) ? null : verificationUrl,
            QrModuleMatrix = qrMatrix,
            QrCodeImage = qrImage
        };

        // Footer ends with legal banner; QR placeholder sits immediately above it.
        var footer = new List<string>();
        if (includeQr)
        {
            footer.Add(Center("Scan to verify on MRA Portal", charactersPerLine));
            footer.Add(QrPlaceholderMarker);
        }

        footer.Add(Center(LegalReceiptEndBanner, charactersPerLine));

        var ordered = new List<string>();
        ordered.AddRange(header);
        ordered.AddRange(meta);
        foreach (var line in lineItems)
        {
            ordered.Add(line.QuantityPriceLine);
            ordered.Add(line.VatBreakdownLine);
        }

        ordered.AddRange(taxLines);
        ordered.AddRange(totals);
        ordered.AddRange(fiscalStatus.BodyLines);
        ordered.AddRange(footer);

        return new MraReceiptLayoutViewModel
        {
            HeaderLines = header,
            MetaLines = meta,
            LineItems = lineItems,
            TaxBreakdownLines = taxLines,
            TotalsLines = totals,
            FiscalStatus = fiscalStatus,
            FooterLines = footer,
            OrderedTextLines = ordered,
            CharactersPerLine = charactersPerLine
        };
    }

    /// <summary>
    /// Renders an official MRA verification QR via QRCoder (module matrix + WPF bitmap).
    /// </summary>
    public static (bool[,]? Matrix, BitmapSource? Image) RenderQrCoderMatrix(
        string? verificationUrl,
        int pixelsPerModule = 4)
    {
        if (string.IsNullOrWhiteSpace(verificationUrl))
        {
            return (null, null);
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(verificationUrl.Trim(), QRCodeGenerator.ECCLevel.Q);
        var matrix = ToBoolMatrix(data.ModuleMatrix);

        var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(Math.Clamp(pixelsPerModule, 2, 12));
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return (matrix, image);
    }

    private static bool[,] ToBoolMatrix(IReadOnlyList<BitArray> moduleMatrix)
    {
        var size = moduleMatrix.Count;
        var matrix = new bool[size, size];
        for (var y = 0; y < size; y++)
        {
            var row = moduleMatrix[y];
            for (var x = 0; x < size && x < row.Length; x++)
            {
                matrix[y, x] = row[x];
            }
        }

        return matrix;
    }

    /// <summary>Rate A always prints as 17.5%; other rate ids keep their code label.</summary>
    private static string FormatStatutoryRateLabel(string rateId) =>
        string.Equals(rateId, "A", StringComparison.OrdinalIgnoreCase)
            ? StatutoryVatPercentLabel
            : StatutoryVatPercentLabel;

    internal static string Separator(char ch, int width) => new(ch, Math.Min(width, 64));

    internal static string Columns(string left, string right, int width)
    {
        left = Truncate(left, Math.Max(1, width - right.Length - 1));
        var spaces = Math.Max(1, width - left.Length - right.Length);
        return left + new string(' ', spaces) + right;
    }

    internal static string Center(string value, int width)
    {
        value = Truncate(value, width);
        if (value.Length >= width)
        {
            return value;
        }

        var pad = width - value.Length;
        var left = pad / 2;
        return new string(' ', left) + value + new string(' ', pad - left);
    }

    internal static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    internal static IEnumerable<string> Chunk(string value, int size)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        for (var i = 0; i < value.Length; i += size)
        {
            yield return value.Substring(i, Math.Min(size, value.Length - i));
        }
    }
}
