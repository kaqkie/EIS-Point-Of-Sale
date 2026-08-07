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
    private readonly IOfflineReceiptQrBridge? _qrBridge;

    public const string LegalReceiptStartBanner = "*** START OF LEGAL RECEIPT ***";
    public const string LegalReceiptEndBanner = "*** END OF LEGAL RECEIPT ***";
    public const string QrPlaceholderMarker = "[MRA FISCAL QR]";
    public const string VatRegisteredBanner = "**VAT REGISTERED**";
    public const string NotVatRegisteredBanner = "**NOT VAT REGISTERED**";

    public MraReceiptLayoutService()
        : this(null)
    {
    }

    public MraReceiptLayoutService(IOfflineReceiptQrBridge? qrBridge)
    {
        _qrBridge = qrBridge;
    }

    public static string StatutoryVatPercentLabel =>
        $"{PosTaxCalculator.MalawiStandardVatRatePercent.ToString("0.0", CultureInfo.InvariantCulture)}%";

    /// <summary>
    /// Exclusive (taxable) unit price for internal math. Prefer
    /// <see cref="ResolveInclusiveUnitPrice"/> for qty × price receipt lines.
    /// </summary>
    public static decimal ResolveExclusiveUnitPrice(InvoiceLineItemDto item)
    {
        var quantity = item.Quantity <= 0m ? 1m : item.Quantity;
        var exclusiveFromTotal = PosTaxCalculator.RoundMoney(
            Math.Max(0m, (item.Total + Math.Max(0m, item.Discount)) / quantity));
        var wireUnit = PosTaxCalculator.RoundMoney(item.UnitPrice);
        if (item.TotalVat > 0m)
        {
            var wireLine = PosTaxCalculator.RoundMoney(wireUnit * quantity);
            var grossLine = PosTaxCalculator.RoundMoney(item.Total + item.TotalVat + Math.Max(0m, item.Discount));
            if (Math.Abs(wireLine - grossLine) <= 0.05m)
            {
                return exclusiveFromTotal;
            }
        }

        return wireUnit > 0m ? wireUnit : exclusiveFromTotal;
    }

    /// <summary>
    /// VAT-inclusive shelf unit price for receipt qty lines (EIS: <c>1 X 20,000.00</c>).
    /// Inventory prices are inclusive; rebuild from exclusive total + VAT when the wire unit is net.
    /// </summary>
    public static decimal ResolveInclusiveUnitPrice(InvoiceLineItemDto item)
    {
        var quantity = item.Quantity <= 0m ? 1m : item.Quantity;
        var inclusiveLine = ResolveInclusiveLineTotal(item);
        var fromTotals = PosTaxCalculator.RoundMoney(inclusiveLine / quantity);
        var wireUnit = PosTaxCalculator.RoundMoney(item.UnitPrice);
        if (wireUnit <= 0m)
        {
            return fromTotals;
        }

        var wireLine = PosTaxCalculator.RoundMoney(wireUnit * quantity);
        // Wire unit already matches inclusive shelf total (Item-mode / inventory price).
        if (Math.Abs(wireLine - inclusiveLine) <= 0.05m)
        {
            return wireUnit;
        }

        return fromTotals;
    }

    /// <summary>VAT-inclusive line amount printed on the receipt (net + VAT).</summary>
    public static decimal ResolveInclusiveLineTotal(InvoiceLineItemDto item) =>
        PosTaxCalculator.RoundMoney(Math.Max(0m, item.Total) + Math.Max(0m, item.TotalVat));

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
        header.Add(Center(
            request.IsVatRegistered == false ? NotVatRegisteredBanner : VatRegisteredBanner,
            charactersPerLine));
        header.Add(Separator('-', charactersPerLine));

        // ---- 2. Buyer placeholders + MRA fiscal receipt number ----
        var buyerTin = string.IsNullOrWhiteSpace(request.BuyerTin) ? "N/A" : request.BuyerTin.Trim();
        var buyerName = string.IsNullOrWhiteSpace(request.BuyerName)
            ? (string.IsNullOrWhiteSpace(request.BuyerTin) ? "WALK-IN CUSTOMER" : "BUSINESS CUSTOMER")
            : request.BuyerName.Trim();
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

        // ---- 3. Itemized breakdown (EIS style: qty X inclusive unit, description, amount+tax) ----
        var lineItems = new List<MraReceiptLineItemViewModel>();
        foreach (var item in request.LineItems)
        {
            var taxCode = string.IsNullOrWhiteSpace(item.TaxRateId) ? "A" : item.TaxRateId.Trim().ToUpperInvariant();
            var inclusiveUnit = ResolveInclusiveUnitPrice(item);
            var inclusiveLineTotal = ResolveInclusiveLineTotal(item);
            var qtyPriceLine = FormatQtyInclusiveUnitLine(item.Quantity, inclusiveUnit, charactersPerLine);
            var descriptionLine = Truncate(item.Description, charactersPerLine);
            var amountLine = Columns(
                string.Empty,
                $"{inclusiveLineTotal:N2} {taxCode}",
                charactersPerLine);

            lineItems.Add(new MraReceiptLineItemViewModel
            {
                Description = descriptionLine,
                QuantityPriceLine = qtyPriceLine,
                VatBreakdownLine = amountLine,
                Quantity = item.Quantity,
                UnitPrice = inclusiveUnit,
                LineTotal = inclusiveLineTotal,
                LineVat = item.TotalVat,
                TaxRateId = taxCode
            });
        }

        // ---- 4. Tax summary: taxable + VAT (statutory A-17.5%) ----
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
        // Prefer local LAN bridge for offline QRs while MRA ReceiptValidation portal returns ISE.
        var qrPayloadUrl = includeQr
            ? (_qrBridge?.RewriteForScan(verificationUrl) ?? verificationUrl)
            : null;

        var fiscalBody = new List<string>();
        if (showOfflinePendingBanner)
        {
            fiscalBody.Add(Center("MRA EIS: OFFLINE QR — sync pending", charactersPerLine));
            fiscalBody.Add(Center(
                _qrBridge?.IsListening == true
                    ? "Scan on store Wi-Fi to verify locally"
                    : "Portal verify works after online sync",
                charactersPerLine));
        }

        if (includeQr)
        {
            fiscalBody.Add(Center(
                isOfflineValidationUrl ? "Offline ValidationURL QR" : "Scan QR to verify with MRA",
                charactersPerLine));
            fiscalBody.Add(QrPlaceholderMarker);
        }

        var (qrMatrix, qrImage) = includeQr
            ? RenderQrCoderMatrix(qrPayloadUrl)
            : (null, null);

        var fiscalStatus = new MraFiscalStatusBlockViewModel
        {
            Title = "MRA EIS FISCAL",
            BodyLines = fiscalBody,
            IsOfflinePending = showOfflinePendingBanner,
            IncludeQrCode = includeQr,
            FiscalSignature = fiscalSignature,
            VerificationUrl = includeQr ? qrPayloadUrl : null,
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
            if (!string.IsNullOrWhiteSpace(line.Description))
            {
                ordered.Add(line.Description);
            }

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
    public static bool IsOfflineValidationUrl(string verificationUrl) =>
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
    /// EIS portal style qty line using VAT-inclusive shelf unit price: <c>1 X 20,000.00</c>.
    /// </summary>
    public static string FormatQtyInclusiveUnitLine(decimal quantity, decimal inclusiveUnitPrice, int width)
    {
        var qty = quantity == decimal.Truncate(quantity)
            ? quantity.ToString("0", CultureInfo.InvariantCulture)
            : quantity.ToString("N2", CultureInfo.InvariantCulture);
        var line = $"{qty} X {inclusiveUnitPrice.ToString("N2", CultureInfo.InvariantCulture)}";
        return Truncate(line, width);
    }

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
