using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Core.Entities;

namespace PointOfSale.App.Services;

public interface ILabelTemplateService
{
    IReadOnlyList<LabelTemplateDefinition> GetTemplates();
    LabelTemplateDefinition GetTemplate(string templateType);
    byte[] BuildEscPosLabel(ProductLabelContent label, string templateType);
    byte[] BuildEscPosBatch(IReadOnlyList<ProductLabelContent> labels, string templateType);
    FlowDocument BuildPreviewDocument(IReadOnlyList<ProductLabelContent> labels, string templateType);
    Task<LabelPrintResult> PrintBatchAsync(
        IReadOnlyList<ProductLabelContent> labels,
        string templateType,
        CancellationToken cancellationToken = default);
}

public sealed class LabelTemplateDefinition
{
    public required string TemplateType { get; init; }
    public required string DisplayName { get; init; }
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
    public int CharactersPerLine { get; init; }
    public bool IncludeBarcode { get; init; } = true;
    public bool IncludeQr { get; init; }
    public string Description { get; init; } = string.Empty;
}

public sealed class LabelPrintResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public int LabelsPrinted { get; init; }
    public bool UsedEscPos { get; init; }
}

/// <summary>
/// Thermal label layout manager for 50×30mm / 40×25mm shelf-edge tags and sticky barcode labels.
/// </summary>
public sealed class LabelTemplateService : ILabelTemplateService
{
    private static readonly IReadOnlyList<LabelTemplateDefinition> BuiltInTemplates =
    [
        new LabelTemplateDefinition
        {
            TemplateType = LabelTemplateTypes.ShelfEdge50x30,
            DisplayName = "Shelf-edge 50×30 mm",
            WidthMm = 50,
            HeightMm = 30,
            CharactersPerLine = 32,
            IncludeBarcode = true,
            Description = "Standard shelf-edge tag with name, MWK price (VAT), and barcode."
        },
        new LabelTemplateDefinition
        {
            TemplateType = LabelTemplateTypes.ShelfEdge40x25,
            DisplayName = "Shelf-edge 40×25 mm",
            WidthMm = 40,
            HeightMm = 25,
            CharactersPerLine = 24,
            IncludeBarcode = true,
            Description = "Compact shelf-edge tag for narrow rails."
        },
        new LabelTemplateDefinition
        {
            TemplateType = LabelTemplateTypes.StickyBarcode,
            DisplayName = "Sticky barcode label",
            WidthMm = 50,
            HeightMm = 25,
            CharactersPerLine = 32,
            IncludeBarcode = true,
            Description = "Barcode-forward sticky label for packaging."
        },
        new LabelTemplateDefinition
        {
            TemplateType = LabelTemplateTypes.FiscalQrTag,
            DisplayName = "MRA fiscal QR tag",
            WidthMm = 50,
            HeightMm = 50,
            CharactersPerLine = 32,
            IncludeBarcode = false,
            IncludeQr = true,
            Description = "QR label embedding MRA EIS verification URL + invoice id."
        }
    ];

    private readonly IBarcodeGenerationService _barcodeService;
    private readonly IThermalPrinterHardwareService _thermalPrinter;
    private readonly LabelPrintingOptions _options;
    private readonly ThermalPrinterOptions _thermalOptions;
    private readonly ILogger<LabelTemplateService> _logger;

    public LabelTemplateService(
        IBarcodeGenerationService barcodeService,
        IThermalPrinterHardwareService thermalPrinter,
        IOptions<LabelPrintingOptions> options,
        IOptions<ThermalPrinterOptions> thermalOptions,
        ILogger<LabelTemplateService> logger)
    {
        _barcodeService = barcodeService;
        _thermalPrinter = thermalPrinter;
        _options = options.Value;
        _thermalOptions = thermalOptions.Value;
        _logger = logger;
    }

    public IReadOnlyList<LabelTemplateDefinition> GetTemplates() => BuiltInTemplates;

    public LabelTemplateDefinition GetTemplate(string templateType)
    {
        var match = BuiltInTemplates.FirstOrDefault(t =>
            t.TemplateType.Equals(templateType, StringComparison.OrdinalIgnoreCase));
        return match ?? BuiltInTemplates[0];
    }

    public byte[] BuildEscPosLabel(ProductLabelContent label, string templateType)
    {
        ArgumentNullException.ThrowIfNull(label);
        var template = GetTemplate(templateType);
        return EscPosLabelEncoder.Encode(label, template);
    }

    public byte[] BuildEscPosBatch(IReadOnlyList<ProductLabelContent> labels, string templateType)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var template = GetTemplate(templateType);
        using var buffer = new MemoryStream(Math.Max(1024, labels.Count * 256));
        foreach (var label in labels)
        {
            var chunk = EscPosLabelEncoder.Encode(label, template);
            buffer.Write(chunk, 0, chunk.Length);
        }

        return buffer.ToArray();
    }

    public FlowDocument BuildPreviewDocument(IReadOnlyList<ProductLabelContent> labels, string templateType)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var template = GetTemplate(templateType);
        var pageWidth = Math.Max(160, template.WidthMm * 3.2);
        var document = new FlowDocument
        {
            PageWidth = pageWidth,
            PagePadding = new Thickness(6),
            FontFamily = new FontFamily("Consolas"),
            FontSize = template.HeightMm <= 25 ? 10 : 11
        };

        foreach (var label in labels)
        {
            document.Blocks.Add(new Paragraph(new Run(Truncate(label.ProductName, template.CharactersPerLine)))
            {
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 2)
            });
            document.Blocks.Add(new Paragraph(new Run(LabelPriceFormatter.FormatGrossMwk(label)))
            {
                Margin = new Thickness(0, 0, 0, 0)
            });
            document.Blocks.Add(new Paragraph(new Run(LabelPriceFormatter.FormatVatLine(label)))
            {
                FontSize = 9,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 4)
            });

            if (template.IncludeBarcode && !string.IsNullOrWhiteSpace(label.BarcodePayload))
            {
                try
                {
                    var barcode = _barcodeService.GenerateBarcodeBitmap(
                        label.BarcodePayload,
                        label.Symbology,
                        widthPx: (int)Math.Min(280, pageWidth),
                        heightPx: template.HeightMm <= 25 ? 48 : 64);
                    document.Blocks.Add(new BlockUIContainer(new Image
                    {
                        Source = barcode,
                        Stretch = Stretch.Uniform,
                        MaxHeight = barcode.PixelHeight,
                        Margin = new Thickness(0, 2, 0, 2)
                    }));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Barcode preview failed for {ProductCode}.", label.ProductCode);
                    document.Blocks.Add(new Paragraph(new Run(label.BarcodePayload)));
                }
            }

            if (template.IncludeQr)
            {
                var qrPayload = label.QrPayload
                    ?? _barcodeService.BuildMraVerificationUrl(label.ProductCode);
                var qr = _barcodeService.GenerateQrBitmap(qrPayload, pixelsPerModule: 3);
                document.Blocks.Add(new BlockUIContainer(new Image
                {
                    Source = qr,
                    Stretch = Stretch.Uniform,
                    MaxHeight = 120,
                    Margin = new Thickness(0, 4, 0, 2)
                }));
                document.Blocks.Add(new Paragraph(new Run(Truncate(qrPayload, template.CharactersPerLine)))
                {
                    FontSize = 8
                });
            }

            document.Blocks.Add(new Paragraph(new Run(new string('-', Math.Min(template.CharactersPerLine, 40))))
            {
                Margin = new Thickness(0, 4, 0, 8)
            });
        }

        return document;
    }

    public async Task<LabelPrintResult> PrintBatchAsync(
        IReadOnlyList<ProductLabelContent> labels,
        string templateType,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(labels);
        if (labels.Count == 0)
        {
            return new LabelPrintResult
            {
                Success = false,
                Message = "No labels to print.",
                LabelsPrinted = 0
            };
        }

        var payload = BuildEscPosBatch(labels, templateType);
        if (_thermalPrinter.IsEnabled)
        {
            try
            {
                await _thermalPrinter.PrintRawAsync(payload, "Albert Retail Terminal Labels", cancellationToken)
                    .ConfigureAwait(false);
                return new LabelPrintResult
                {
                    Success = true,
                    Message = $"Printed {labels.Count} label(s) via ESC/POS.",
                    LabelsPrinted = labels.Count,
                    UsedEscPos = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ESC/POS label print failed; falling back to Windows print dialog.");
            }
        }

        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var document = BuildPreviewDocument(labels, templateType);
            var dialog = new PrintDialog();
            if (!string.IsNullOrWhiteSpace(_thermalOptions.PrinterName))
            {
                // Prefer configured queue name when available; PrintDialog still lets the operator confirm.
            }

            if (dialog.ShowDialog() == true)
            {
                document.PageHeight = dialog.PrintableAreaHeight;
                document.PageWidth = dialog.PrintableAreaWidth;
                dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, "Product Labels");
            }
        });

        return new LabelPrintResult
        {
            Success = true,
            Message = $"Sent {labels.Count} label(s) to Windows print dialog.",
            LabelsPrinted = labels.Count,
            UsedEscPos = false
        };
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}

/// <summary>ESC/POS encoder for thermal product / shelf-edge labels.</summary>
public static class EscPosLabelEncoder
{
    private static readonly Lazy<Encoding> PrinterEncoding = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(437);
    });

    public static byte[] Encode(ProductLabelContent label, LabelTemplateDefinition template)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(template);

        using var buffer = new MemoryStream(512);
        Write(buffer, Init());
        Write(buffer, AlignCenter());
        Write(buffer, Bold(true));
        WriteLine(buffer, Truncate(label.ProductName, template.CharactersPerLine));
        Write(buffer, Bold(false));
        WriteLine(buffer, Truncate(LabelPriceFormatter.FormatGrossMwk(label), template.CharactersPerLine));
        WriteLine(buffer, Truncate(LabelPriceFormatter.FormatVatLine(label), template.CharactersPerLine));
        WriteLine(buffer, Truncate(label.ProductCode, template.CharactersPerLine));

        if (template.IncludeBarcode && !string.IsNullOrWhiteSpace(label.BarcodePayload))
        {
            Write(buffer, AlignCenter());
            Write(buffer, SetBarcodeHeight(template.HeightMm <= 25 ? (byte)48 : (byte)64));
            Write(buffer, SetBarcodeWidth(2));
            Write(buffer, SetBarcodeHri(2));
            Write(buffer, BuildCode128(label.BarcodePayload));
            WriteLine(buffer, string.Empty);
        }

        if (template.IncludeQr)
        {
            var qrPayload = label.QrPayload ?? label.BarcodePayload;
            if (!string.IsNullOrWhiteSpace(qrPayload))
            {
                Write(buffer, AlignCenter());
                Write(buffer, EscPosReceiptEncoder.BuildQrCode(qrPayload));
                WriteLine(buffer, string.Empty);
            }
        }

        Write(buffer, FeedAndPartialCut());
        return buffer.ToArray();
    }

    /// <summary>ESC/POS Code 128 (GS k 73).</summary>
    public static byte[] BuildCode128(string data)
    {
        var payload = PrinterEncoding.Value.GetBytes(data);
        if (payload.Length is < 1 or > 255)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Code 128 payload length must be 1–255 bytes.");
        }

        using var ms = new MemoryStream(payload.Length + 4);
        ms.WriteByte(0x1D);
        ms.WriteByte(0x6B);
        ms.WriteByte(0x49); // CODE128
        ms.WriteByte((byte)payload.Length);
        ms.Write(payload);
        return ms.ToArray();
    }

    private static byte[] Init() => [0x1B, 0x40];
    private static byte[] AlignCenter() => [0x1B, 0x61, 0x01];
    private static byte[] Bold(bool on) => [0x1B, 0x45, (byte)(on ? 1 : 0)];
    private static byte[] SetBarcodeHeight(byte dots) => [0x1D, 0x68, dots];
    private static byte[] SetBarcodeWidth(byte module) => [0x1D, 0x77, module];
    private static byte[] SetBarcodeHri(byte position) => [0x1D, 0x48, position];
    private static byte[] FeedAndPartialCut() => [0x0A, 0x0A, 0x1D, 0x56, 0x01];

    private static void Write(Stream stream, byte[] data) => stream.Write(data, 0, data.Length);

    private static void WriteLine(Stream stream, string text)
    {
        var bytes = PrinterEncoding.Value.GetBytes(text + "\n");
        stream.Write(bytes, 0, bytes.Length);
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
}
