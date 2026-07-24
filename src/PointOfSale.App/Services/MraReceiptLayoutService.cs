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
/// Builds the organized MRA thermal receipt layout: column-aligned totals,
/// item-level VAT 17.5% breakdowns, fiscal status blocks, and QRCoder verification QR.
/// </summary>
public sealed class MraReceiptLayoutService : IMraReceiptLayoutService
{
    public static string StatutoryVatPercentLabel =>
        $"{PosTaxCalculator.MalawiStandardVatRatePercent:0.0}%";

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

        var header = new List<string>
        {
            Separator('=', charactersPerLine),
            Center(Truncate(request.TradingName, charactersPerLine), charactersPerLine)
        };
        foreach (var address in request.AddressLines.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            header.Add(Center(Truncate(address.Trim(), charactersPerLine), charactersPerLine));
        }

        header.Add(Center(Truncate($"TIN: {request.SellerTin}", charactersPerLine), charactersPerLine));
        header.Add(Separator('-', charactersPerLine));

        var meta = new List<string>
        {
            Truncate($"Invoice: {request.InvoiceNumber}", charactersPerLine),
            Truncate($"Date: {request.InvoiceDateTime:yyyy-MM-dd HH:mm}", charactersPerLine),
            Separator('-', charactersPerLine),
            Columns("ITEM", "AMOUNT", charactersPerLine)
        };

        var lineItems = new List<MraReceiptLineItemViewModel>();
        foreach (var item in request.LineItems)
        {
            var qtyPrice = $"{item.Quantity:N2} x {item.UnitPrice:N2}";
            var amount = $"{item.Total:N2}";
            var qtyPriceLine = Columns(qtyPrice, amount, charactersPerLine);
            var vatLine = Columns(
                $"  VAT {StatutoryVatPercentLabel}",
                $"{item.TotalVat:N2}",
                charactersPerLine);

            lineItems.Add(new MraReceiptLineItemViewModel
            {
                Description = Truncate(item.Description, charactersPerLine),
                QuantityPriceLine = qtyPriceLine,
                VatBreakdownLine = vatLine,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                LineTotal = item.Total,
                LineVat = item.TotalVat,
                TaxRateId = item.TaxRateId
            });
        }

        var taxLines = new List<string> { Separator('-', charactersPerLine) };
        foreach (var tax in request.TaxBreakdown)
        {
            var rateLabel = FormatTaxRateLabel(tax.RateId);
            taxLines.Add(Truncate($"Tax {tax.RateId} ({rateLabel})", charactersPerLine));
            taxLines.Add(Columns("  Taxable", $"{tax.TaxableAmount:N2}", charactersPerLine));
            taxLines.Add(Columns($"  VAT {StatutoryVatPercentLabel}", $"{tax.TaxAmount:N2}", charactersPerLine));
        }

        var totals = new List<string>
        {
            Separator('-', charactersPerLine),
            Columns("Subtotal", $"{request.ResolveSubtotalNet():N2}", charactersPerLine),
            Columns($"VAT {StatutoryVatPercentLabel}", $"{request.ResolveTotalVat():N2}", charactersPerLine),
            Columns("TOTAL", $"{request.InvoiceTotal:N2}", charactersPerLine),
            Columns("Tendered", $"{request.AmountTendered:N2}", charactersPerLine),
            Columns("Change", $"{request.ChangeDue:N2}", charactersPerLine),
            Separator('=', charactersPerLine)
        };

        var fiscalBody = new List<string>();
        if (isOfflinePending)
        {
            fiscalBody.Add("OFFLINE — queued for sync");
            foreach (var chunk in Chunk(string.IsNullOrWhiteSpace(fiscalSignature)
                         ? FiscalReceiptEnricher.OfflinePendingPlaceholder
                         : fiscalSignature,
                     charactersPerLine))
            {
                fiscalBody.Add(chunk);
            }

            fiscalBody.Add("(QR prints after MRA sync)");
        }
        else
        {
            fiscalBody.Add("SYNCED — fiscal signature");
            foreach (var chunk in Chunk(fiscalSignature, charactersPerLine))
            {
                fiscalBody.Add(chunk);
            }

            if (!string.IsNullOrWhiteSpace(verificationUrl))
            {
                fiscalBody.Add("Verify:");
                foreach (var chunk in Chunk(verificationUrl, charactersPerLine))
                {
                    fiscalBody.Add(chunk);
                }

                if (includeQr)
                {
                    fiscalBody.Add("Scan MRA verification QR");
                }
            }
        }

        var fiscalStatus = new MraFiscalStatusBlockViewModel
        {
            Title = "*** MRA EIS FISCAL STATUS ***",
            BodyLines = fiscalBody,
            IsOfflinePending = isOfflinePending,
            IncludeQrCode = includeQr,
            FiscalSignature = fiscalSignature,
            VerificationUrl = string.IsNullOrWhiteSpace(verificationUrl) ? null : verificationUrl,
            QrModuleMatrix = qrMatrix,
            QrCodeImage = qrImage
        };

        var footer = new List<string>
        {
            Separator('-', charactersPerLine),
            Center("Thank you", charactersPerLine),
            Center("Albert Retail Terminal", charactersPerLine)
        };

        var ordered = new List<string>();
        ordered.AddRange(header);
        ordered.AddRange(meta);
        foreach (var line in lineItems)
        {
            ordered.Add(line.Description);
            ordered.Add(line.QuantityPriceLine);
            ordered.Add(line.VatBreakdownLine);
        }

        ordered.AddRange(taxLines);
        ordered.AddRange(totals);
        ordered.Add(Center(fiscalStatus.Title, charactersPerLine));
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
    /// Returns nulls when the URL is empty (offline queue / missing fiscal payload).
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

    private static string FormatTaxRateLabel(string? rateId) =>
        string.Equals(rateId, "A", StringComparison.OrdinalIgnoreCase)
            ? StatutoryVatPercentLabel
            : rateId?.Trim() is { Length: > 0 } id
                ? id
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
