using System.Collections;
using System.Globalization;
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
        $"{PosTaxCalculator.MalawiStandardVatRatePercent.ToString("0.0", CultureInfo.InvariantCulture)}%";

    /// <summary>
    /// Formats the seller TIN from active store/terminal configuration.
    /// Empty or sandbox-placeholder values print as NOT CONFIGURED.
    /// </summary>
    public static string FormatSellerTin(string? sellerTin)
    {
        if (string.IsNullOrWhiteSpace(sellerTin)
            || PosConfigurationService.IsPlaceholderTaxpayerTin(sellerTin))
        {
            return "NOT CONFIGURED";
        }

        return sellerTin.Trim();
    }

    /// <summary>Formats optional merchant contact fields; empty values print as NOT CONFIGURED.</summary>
    public static string FormatConfiguredValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "NOT CONFIGURED" : value.Trim();

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
        var isOfflinePending = FiscalReceiptEnricher.IsOfflinePlaceholder(fiscalSignature);
        var fiscalReceiptNumber = request.ResolveFiscalReceiptNumber();
        var paymentLabel = request.ResolvePaymentMethodLabel();

        var localTime = request.InvoiceDateTime.Kind == DateTimeKind.Utc
            ? request.InvoiceDateTime.ToLocalTime()
            : request.InvoiceDateTime;

        // ---- 1. Header: business name, address, contact, Merchant TIN ----
        var header = new List<string>
        {
            Center(LegalReceiptStartBanner, charactersPerLine),
            Center("MALAWI REVENUE AUTHORITY", charactersPerLine),
            Center("Electronic Invoicing System (EIS)", charactersPerLine),
            Separator('-', charactersPerLine),
            Center(Truncate(request.TradingName, charactersPerLine), charactersPerLine)
        };

        var addressLines = request.AddressLines
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();
        if (addressLines.Count == 0)
        {
            header.Add(Center("Address: NOT CONFIGURED", charactersPerLine));
        }
        else
        {
            foreach (var address in addressLines)
            {
                header.Add(Center(Truncate(address, charactersPerLine), charactersPerLine));
            }
        }

        header.Add(Center(
            Truncate($"MOB: {FormatConfiguredValue(request.ContactPhone)}", charactersPerLine),
            charactersPerLine));
        header.Add(Center(
            Truncate($"Email: {FormatConfiguredValue(request.ContactEmail)}", charactersPerLine),
            charactersPerLine));
        header.Add(Center(
            Truncate($"Merchant TIN: {FormatSellerTin(request.SellerTin)}", charactersPerLine),
            charactersPerLine));
        header.Add(Center(VatRegisteredBanner, charactersPerLine));
        header.Add(Separator('-', charactersPerLine));

        // ---- 2. Buyer placeholders + MRA fiscal receipt number ----
        var buyerTin = string.IsNullOrWhiteSpace(request.BuyerTin) ? "N/A" : request.BuyerTin.Trim();
        var buyerName = string.IsNullOrWhiteSpace(request.BuyerName) ? "WALK-IN CUSTOMER" : request.BuyerName.Trim();
        var meta = new List<string>
        {
            Truncate($"Buyer's TIN: {buyerTin}", charactersPerLine),
            Truncate($"Buyer's Name: {buyerName}", charactersPerLine),
            Truncate($"FISCAL RECEIPT NUMBER: {fiscalReceiptNumber}", charactersPerLine),
            Truncate($"Date/Time: {localTime:yyyy-MM-dd HH:mm:ss}", charactersPerLine),
            Separator('-', charactersPerLine),
            // Clear thermal columns: QTY | DESCRIPTION ........ AMOUNT
            ItemHeaderLine(charactersPerLine)
        };

        // ---- 3. Itemized breakdown: qty, description, pricing ----
        var lineItems = new List<MraReceiptLineItemViewModel>();
        foreach (var item in request.LineItems)
        {
            var taxCode = string.IsNullOrWhiteSpace(item.TaxRateId) ? "A" : item.TaxRateId.Trim().ToUpperInvariant();
            var qtyPriceLine = FormatItemRow(item.Quantity, item.Description, item.Total, charactersPerLine);
            var detailLine = Columns(
                $"  @ {item.UnitPrice:N2} x {item.Quantity:N2}",
                $"VAT-{taxCode}",
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

        // ---- 4. Tax summary: taxable + VAT (EIS sample: TAXABLE A-16.5% / VAT A-16.5%) ----
        var taxLines = new List<string> { Separator('-', charactersPerLine) };
        if (request.TaxBreakdown.Count == 0)
        {
            taxLines.Add(Columns(
                $"TAXABLE A-{StatutoryVatPercentLabel}",
                $"{request.ResolveSubtotalNet():N2}",
                charactersPerLine));
            taxLines.Add(Columns(
                $"VAT A-{StatutoryVatPercentLabel}",
                $"{request.ResolveTotalVat():N2}",
                charactersPerLine));
        }
        else
        {
            foreach (var tax in request.TaxBreakdown)
            {
                var rateId = string.IsNullOrWhiteSpace(tax.RateId) ? "A" : tax.RateId.Trim().ToUpperInvariant();
                var rateLabel = FormatRatePercentLabel(rateId, tax.TaxableAmount, tax.TaxAmount);
                taxLines.Add(Columns(
                    $"TAXABLE {rateId}-{rateLabel}",
                    $"{tax.TaxableAmount:N2}",
                    charactersPerLine));
                taxLines.Add(Columns(
                    $"VAT {rateId}-{rateLabel}",
                    $"{tax.TaxAmount:N2}",
                    charactersPerLine));
            }
        }

        // ---- 5. Payment + tendered/change + totals ----
        var totals = new List<string>
        {
            Separator('-', charactersPerLine),
            Columns("TOTAL VAT", $"{request.ResolveTotalVat():N2}", charactersPerLine),
            Columns("GRAND TOTAL", $"{request.InvoiceTotal:N2}", charactersPerLine),
            Separator('-', charactersPerLine),
            Truncate($"PAYMENT METHOD: {paymentLabel}", charactersPerLine),
            Columns("AMOUNT TENDERED", $"{request.AmountTendered:N2}", charactersPerLine),
            Columns("CHANGE", $"{request.ChangeDue:N2}", charactersPerLine),
            Truncate($"TRANSACTION DATE/TIME: {localTime:yyyy-MM-dd HH:mm:ss}", charactersPerLine),
            Separator('-', charactersPerLine)
        };

        // ---- 6. Fiscal status: offline pending banner and/or MRA verification QR ----
        var verificationUrl = fiscal?.ResolveVerificationUrl();
        var hasPrintableVerificationUrl = !string.IsNullOrWhiteSpace(verificationUrl)
            && !FiscalReceiptEnricher.IsOfflinePlaceholder(verificationUrl);
        var isOfflineValidationUrl = hasPrintableVerificationUrl
            && IsOfflineValidationUrl(verificationUrl!);
        // True placeholder (no HMAC yet) or offline ValidationURL while still queued for EIS sync.
        var showOfflinePendingBanner = isOfflinePending || isOfflineValidationUrl;
        var includeQr = hasPrintableVerificationUrl;

        var fiscalBody = new List<string>();
        if (showOfflinePendingBanner)
        {
            fiscalBody.Add(Center("MRA EIS: OFFLINE — queued for sync", charactersPerLine));
        }

        if (includeQr)
        {
            fiscalBody.Add(Center("Scan QR to verify with MRA", charactersPerLine));
            fiscalBody.Add(QrPlaceholderMarker);
        }

        var (qrMatrix, qrImage) = includeQr
            ? RenderQrCoderMatrix(verificationUrl)
            : (null, null);

        var fiscalStatus = new MraFiscalStatusBlockViewModel
        {
            Title = "MRA EIS FISCAL",
            BodyLines = fiscalBody,
            IsOfflinePending = showOfflinePendingBanner,
            IncludeQrCode = includeQr,
            FiscalSignature = fiscalSignature,
            VerificationUrl = includeQr ? verificationUrl : null,
            QrModuleMatrix = qrMatrix,
            QrCodeImage = qrImage
        };

        var footer = new List<string>
        {
            Center(LegalReceiptEndBanner, charactersPerLine)
        };

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
    /// Offline ValidationURL hosts use ReceiptValidation/Validate (HMAC <c>S=</c>), distinct from portal verify links.
    /// </summary>
    internal static bool IsOfflineValidationUrl(string verificationUrl) =>
        verificationUrl.Contains("ReceiptValidation", StringComparison.OrdinalIgnoreCase)
        || verificationUrl.Contains("/Validate", StringComparison.OrdinalIgnoreCase)
        || verificationUrl.Contains("&S=", StringComparison.Ordinal)
        || verificationUrl.Contains("?S=", StringComparison.Ordinal);

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

    /// <summary>Prefer the rate implied by taxable/VAT amounts; else statutory configured percent.</summary>
    private static string FormatRatePercentLabel(string rateId, decimal taxableAmount, decimal taxAmount)
    {
        if (taxableAmount > 0m && taxAmount >= 0m)
        {
            var implied = Math.Round(taxAmount * 100m / taxableAmount, 1, MidpointRounding.AwayFromZero);
            if (implied is >= 0m and <= 100m)
            {
                return $"{implied.ToString("0.0", CultureInfo.InvariantCulture)}%";
            }
        }

        return string.Equals(rateId, "A", StringComparison.OrdinalIgnoreCase)
            ? StatutoryVatPercentLabel
            : StatutoryVatPercentLabel;
    }

    /// <summary>Column header for qty / description / amount itemization.</summary>
    internal static string ItemHeaderLine(int width) =>
        Columns("QTY  DESCRIPTION", "AMOUNT", width);

    /// <summary>
    /// Formats one sale line as quantity + description (left) and line amount (right).
    /// </summary>
    internal static string FormatItemRow(decimal quantity, string description, decimal lineTotal, int width)
    {
        var qty = quantity.ToString("N2", CultureInfo.InvariantCulture);
        var left = $"{qty} {description}".Trim();
        var right = lineTotal.ToString("N2", CultureInfo.InvariantCulture);
        return Columns(left, right, width);
    }

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
