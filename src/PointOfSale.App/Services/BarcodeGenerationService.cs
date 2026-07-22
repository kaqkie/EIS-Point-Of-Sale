using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.Rendering;

namespace PointOfSale.App.Services;

public interface IBarcodeGenerationService
{
    string ResolveSymbology(string productCode);
    string NormalizeBarcodePayload(string productCode, string symbology);
    string BuildMraVerificationUrl(string invoiceNumber, string? fiscalSignature = null);
    ProductLabelContent BuildLabelContent(LocalInventoryItem product, bool? showVatInclusive = null);
    BitmapSource GenerateBarcodeBitmap(string payload, string symbology, int? widthPx = null, int? heightPx = null);
    BitmapSource GenerateQrBitmap(string payload, int pixelsPerModule = 4);
    byte[] GenerateBarcodePng(string payload, string symbology, int? widthPx = null, int? heightPx = null);
    byte[] GenerateQrPng(string payload, int pixelsPerModule = 4);
    IReadOnlyList<ProductLabelContent> BuildBatchLabels(
        IEnumerable<LocalInventoryItem> products,
        int quantityPerItem);
}

/// <summary>
/// ZXing-backed barcode / QR engine for product SKUs and MRA fiscal verification payloads.
/// </summary>
public sealed class BarcodeGenerationService : IBarcodeGenerationService
{
    private readonly LabelPrintingOptions _options;

    public BarcodeGenerationService(IOptions<LabelPrintingOptions> options)
    {
        _options = options.Value;
    }

    public string ResolveSymbology(string productCode)
    {
        var digits = new string((productCode ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length is 12 or 13)
        {
            return BarcodeSymbologies.Ean13;
        }

        return BarcodeSymbologies.Code128;
    }

    public string NormalizeBarcodePayload(string productCode, string symbology)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productCode);
        if (string.Equals(symbology, BarcodeSymbologies.Ean13, StringComparison.OrdinalIgnoreCase))
        {
            var digits = new string(productCode.Where(char.IsDigit).ToArray());
            if (digits.Length == 12)
            {
                return digits + ComputeEan13CheckDigit(digits);
            }

            if (digits.Length == 13)
            {
                return digits;
            }

            throw new InvalidOperationException(
                $"Product code '{productCode}' is not a valid 12/13-digit EAN-13 payload.");
        }

        return productCode.Trim().ToUpperInvariant();
    }

    public string BuildMraVerificationUrl(string invoiceNumber, string? fiscalSignature = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
        var baseUrl = (_options.MraVerificationBaseUrl ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "https://eis.mra.mw/verify";
        }

        var url = $"{baseUrl}?invoice={Uri.EscapeDataString(invoiceNumber.Trim())}";
        if (!string.IsNullOrWhiteSpace(fiscalSignature))
        {
            url += $"&sig={Uri.EscapeDataString(fiscalSignature.Trim())}";
        }

        return url;
    }

    public ProductLabelContent BuildLabelContent(LocalInventoryItem product, bool? showVatInclusive = null)
    {
        ArgumentNullException.ThrowIfNull(product);
        var symbology = ResolveSymbology(product.ProductCode);
        var payload = NormalizeBarcodePayload(product.ProductCode, symbology);
        var (net, vat, gross) = PosTaxCalculator.MapUnitPriceLine(
            product.UnitPrice,
            quantity: 1m,
            PosTaxCalculator.MalawiStandardVatRatePercent);

        return new ProductLabelContent
        {
            ProductCode = product.ProductCode,
            ProductName = product.Name,
            UnitPriceNet = net,
            VatAmount = vat,
            UnitPriceGross = gross,
            VatRatePercent = PosTaxCalculator.MalawiStandardVatRatePercent,
            Symbology = symbology,
            BarcodePayload = payload,
            ShowVatInclusive = showVatInclusive ?? _options.ShowVatInclusivePrice
        };
    }

    public BitmapSource GenerateBarcodeBitmap(string payload, string symbology, int? widthPx = null, int? heightPx = null)
    {
        var format = ResolveFormat(symbology);
        var writer = new BarcodeWriterPixelData
        {
            Format = format,
            Options = new EncodingOptions
            {
                Width = widthPx ?? _options.PreviewBarcodeWidthPx,
                Height = heightPx ?? _options.PreviewBarcodeHeightPx,
                Margin = 2,
                PureBarcode = false
            }
        };

        var pixelData = writer.Write(payload);
        return ToBitmapSource(pixelData);
    }

    public BitmapSource GenerateQrBitmap(string payload, int pixelsPerModule = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Width = Math.Max(64, pixelsPerModule * 33),
                Height = Math.Max(64, pixelsPerModule * 33),
                Margin = 1,
                ErrorCorrection = ZXing.QrCode.Internal.ErrorCorrectionLevel.M,
                CharacterSet = "UTF-8"
            }
        };

        return ToBitmapSource(writer.Write(payload));
    }

    public byte[] GenerateBarcodePng(string payload, string symbology, int? widthPx = null, int? heightPx = null)
    {
        var bitmap = GenerateBarcodeBitmap(payload, symbology, widthPx, heightPx);
        return EncodePng(bitmap);
    }

    public byte[] GenerateQrPng(string payload, int pixelsPerModule = 4)
    {
        var bitmap = GenerateQrBitmap(payload, pixelsPerModule);
        return EncodePng(bitmap);
    }

    public IReadOnlyList<ProductLabelContent> BuildBatchLabels(
        IEnumerable<LocalInventoryItem> products,
        int quantityPerItem)
    {
        ArgumentNullException.ThrowIfNull(products);
        quantityPerItem = Math.Max(1, quantityPerItem);
        var labels = new List<ProductLabelContent>();
        foreach (var product in products)
        {
            var content = BuildLabelContent(product);
            for (var i = 0; i < quantityPerItem; i++)
            {
                labels.Add(content);
            }
        }

        return labels;
    }

    public static int ComputeEan13CheckDigit(string twelveDigits)
    {
        if (twelveDigits.Length != 12 || twelveDigits.Any(c => !char.IsDigit(c)))
        {
            throw new ArgumentException("EAN-13 base must be exactly 12 digits.", nameof(twelveDigits));
        }

        var sum = 0;
        for (var i = 0; i < 12; i++)
        {
            var digit = twelveDigits[i] - '0';
            sum += (i % 2 == 0) ? digit : digit * 3;
        }

        var mod = sum % 10;
        return mod == 0 ? 0 : 10 - mod;
    }

    private static BarcodeFormat ResolveFormat(string symbology) =>
        string.Equals(symbology, BarcodeSymbologies.Ean13, StringComparison.OrdinalIgnoreCase)
            ? BarcodeFormat.EAN_13
            : BarcodeFormat.CODE_128;

    private static BitmapSource ToBitmapSource(PixelData pixelData)
    {
        var dpi = 96d;
        var bitmap = BitmapSource.Create(
            pixelData.Width,
            pixelData.Height,
            dpi,
            dpi,
            PixelFormats.Bgra32,
            null,
            pixelData.Pixels,
            pixelData.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] EncodePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}

/// <summary>Display helpers for label price lines (MWK + VAT).</summary>
public static class LabelPriceFormatter
{
    public static string FormatGrossMwk(ProductLabelContent label) =>
        string.Create(CultureInfo.InvariantCulture, $"{label.UnitPriceGross:N2} MWK");

    public static string FormatVatLine(ProductLabelContent label) =>
        label.ShowVatInclusive
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"incl. {label.VatRatePercent:N1}% VAT ({label.VatAmount:N2})")
            : string.Create(
                CultureInfo.InvariantCulture,
                $"excl. VAT {label.UnitPriceNet:N2} MWK");
}
