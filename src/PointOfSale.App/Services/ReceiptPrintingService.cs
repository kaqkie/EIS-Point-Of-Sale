using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.App.Services;

public interface IReceiptPrintingService
{
    ReceiptPrintResult BuildReceipt(ReceiptPrintRequest request);
    Task PrintAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default);
}

public sealed class ReceiptPrintingService : IReceiptPrintingService
{
    private readonly IThermalPrinterHardwareService _thermalPrinter;
    private readonly IMraReceiptLayoutService _layoutService;
    private readonly ThermalPrinterOptions _thermalOptions;
    private readonly ILogger<ReceiptPrintingService> _logger;

    public ReceiptPrintingService(
        IThermalPrinterHardwareService thermalPrinter,
        IMraReceiptLayoutService layoutService,
        IOptions<ThermalPrinterOptions> thermalOptions,
        ILogger<ReceiptPrintingService> logger)
    {
        _thermalPrinter = thermalPrinter;
        _layoutService = layoutService;
        _thermalOptions = thermalOptions.Value;
        _logger = logger;
    }

    public ReceiptPrintResult BuildReceipt(ReceiptPrintRequest request)
    {
        var charactersPerLine = _thermalOptions.PaperWidthMm <= 58 ? 32 : 42;
        var layout = _layoutService.Build(request, charactersPerLine);
        var fiscal = layout.FiscalStatus;
        var pageWidth = PrintPageSizeGuard.ResolveThermalWidthDip(_thermalOptions.PaperWidthMm);

        var document = new FlowDocument
        {
            PageWidth = pageWidth,
            PageHeight = PrintPageSizeGuard.DefaultReceiptHeightDip,
            PagePadding = new Thickness(8),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            ColumnWidth = Math.Max(72, pageWidth - 16)
        };

        foreach (var text in layout.OrderedTextLines)
        {
            if (string.Equals(text, MraReceiptLayoutService.QrPlaceholderMarker, StringComparison.Ordinal))
            {
                if (fiscal.IncludeQrCode && fiscal.QrCodeImage is not null)
                {
                    document.Blocks.Add(CreateQrBlock(fiscal.QrCodeImage));
                }

                continue;
            }

            if (text.Contains("START OF LEGAL RECEIPT", StringComparison.Ordinal)
                || text.Contains("END OF LEGAL RECEIPT", StringComparison.Ordinal)
                || text.Contains("VAT REGISTERED", StringComparison.Ordinal)
                || text.Contains("GRAND TOTAL", StringComparison.Ordinal)
                || text.Contains("FISCAL RECEIPT NUMBER", StringComparison.Ordinal)
                || text.StartsWith("TOTAL VAT", StringComparison.Ordinal)
                || text.StartsWith("TOTAL", StringComparison.Ordinal))
            {
                document.Blocks.Add(HeadingInline(text.Trim()));
            }
            else
            {
                document.Blocks.Add(Mono(text));
            }
        }

        return new ReceiptPrintResult(
            document,
            fiscal.QrCodeImage,
            fiscal.FiscalSignature,
            fiscal.VerificationUrl ?? string.Empty);
    }

    public async Task PrintAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default)
    {
        if (_thermalPrinter.IsEnabled)
        {
            try
            {
                await _thermalPrinter.PrintReceiptAsync(request, cancellationToken).ConfigureAwait(true);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ESC/POS thermal print failed; falling back to Windows FlowDocument print.");
            }
        }

        await PrintFlowDocumentAsync(request).ConfigureAwait(true);
    }

    private Task PrintFlowDocumentAsync(ReceiptPrintRequest request)
    {
        var receipt = BuildReceipt(request);
        var printDialog = new PrintDialog();
        if (!string.IsNullOrWhiteSpace(_thermalOptions.PrinterName))
        {
            using var server = new LocalPrintServer();
            printDialog.PrintQueue = server.GetPrintQueue(_thermalOptions.PrinterName.Trim());
        }

        if (printDialog.PrintQueue is null)
        {
            printDialog.PrintQueue = LocalPrintServer.GetDefaultPrintQueue();
        }

        if (printDialog.PrintQueue is null)
        {
            throw new InvalidOperationException("No default printer is configured for thermal receipt printing.");
        }

        var fallbackWidth = PrintPageSizeGuard.ResolveThermalWidthDip(_thermalOptions.PaperWidthMm);
        var fallbackHeight = EstimateReceiptHeightDip(request);
        PrintPageSizeGuard.ApplySafePageSize(receipt.Document, printDialog, fallbackWidth, fallbackHeight);
        PrintPageSizeGuard.EnsureDocumentReadyToPrint(receipt.Document);

        printDialog.PrintDocument(
            ((IDocumentPaginatorSource)receipt.Document).DocumentPaginator,
            "Albert Retail Terminal Receipt");
        return Task.CompletedTask;
    }

    private double EstimateReceiptHeightDip(ReceiptPrintRequest request)
    {
        var charactersPerLine = _thermalOptions.PaperWidthMm <= 58 ? 32 : 42;
        var layout = _layoutService.Build(request, charactersPerLine);
        var qrBlock = layout.FiscalStatus.IncludeQrCode ? 140 : 0;
        var estimated = 96 + (layout.OrderedTextLines.Count * 16) + qrBlock;
        return PrintPageSizeGuard.Sanitize(
            estimated,
            PrintPageSizeGuard.DefaultReceiptHeightDip,
            PrintPageSizeGuard.MinPageDimensionDip,
            PrintPageSizeGuard.MaxPageHeightDip);
    }

    private static BlockUIContainer CreateQrBlock(BitmapSource qr)
    {
        return new BlockUIContainer
        {
            Child = new Image
            {
                Source = qr,
                Width = 120,
                Height = 120,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
    }

    private static Paragraph Heading(string text) =>
        new(new Run(text) { FontWeight = FontWeights.Bold, FontSize = 14 })
        {
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };

    private static Paragraph HeadingInline(string text) =>
        new(new Run(text) { FontWeight = FontWeights.Bold, FontSize = 12 })
        {
            Margin = new Thickness(0, 2, 0, 2)
        };

    private static Paragraph Mono(string text) =>
        new(new Run(text)) { Margin = new Thickness(0, 0, 0, 1) };
}

public sealed class ReceiptPrintRequest
{
    public required string TradingName { get; init; }
    public required string SellerTin { get; init; }
    public required IReadOnlyList<string> AddressLines { get; init; }
    public required string InvoiceNumber { get; init; }
    public DateTime InvoiceDateTime { get; init; }
    public required IReadOnlyList<InvoiceLineItemDto> LineItems { get; init; }
    public required IReadOnlyList<TaxBreakDownDto> TaxBreakdown { get; init; }
    public decimal InvoiceTotal { get; init; }
    public decimal AmountTendered { get; init; }

    /// <summary>Net merchandise total before VAT (MWK).</summary>
    public decimal SubtotalNet { get; init; }

    /// <summary>Total VAT collected (statutory 17.5% for rate A in Malawi).</summary>
    public decimal TotalVat { get; init; }

    public SubmitSalesTransactionResponseData? FiscalResponse { get; init; }

    /// <summary>Buyer TIN for MRA legal receipt metadata (N/A when walk-in).</summary>
    public string? BuyerTin { get; init; }

    /// <summary>Buyer name for MRA legal receipt metadata.</summary>
    public string? BuyerName { get; init; }

    /// <summary>Vendor contact phone printed in the legal receipt header.</summary>
    public string? ContactPhone { get; init; }

    /// <summary>Vendor contact email printed in the legal receipt header.</summary>
    public string? ContactEmail { get; init; }

    /// <summary>Payment method printed on the legal receipt (e.g. CASH, CARD, MOBILE MONEY).</summary>
    public string? PaymentMethod { get; init; }

    public decimal ResolveSubtotalNet() =>
        SubtotalNet > 0m
            ? SubtotalNet
            : LineItems.Sum(i => i.Total - i.TotalVat);

    public decimal ResolveTotalVat() =>
        TotalVat > 0m
            ? TotalVat
            : TaxBreakdown.Sum(t => t.TaxAmount);

    public decimal ChangeDue => Math.Max(0m, AmountTendered - InvoiceTotal);

    /// <summary>
    /// MRA-assigned fiscal receipt / invoice number (composite Base64 form, e.g. <c>CV-WEB-JY4+-C</c>).
    /// Prefers the EIS response invoice number when present.
    /// </summary>
    public string ResolveFiscalReceiptNumber()
    {
        var fromEis = FiscalResponse?.InvoiceNumber?.Trim();
        if (!string.IsNullOrWhiteSpace(fromEis))
        {
            return fromEis;
        }

        return string.IsNullOrWhiteSpace(InvoiceNumber) ? "PENDING" : InvoiceNumber.Trim();
    }

    /// <summary>Normalizes payment method for thermal print (uppercase legal label).</summary>
    public string ResolvePaymentMethodLabel()
    {
        if (string.IsNullOrWhiteSpace(PaymentMethod))
        {
            return "CASH";
        }

        var normalized = PaymentMethod.Trim();
        return normalized.ToUpperInvariant() switch
        {
            "MOBILEMONEY" or "MOBILE_MONEY" or "MOBILE MONEY" => "MOBILE MONEY",
            "CARD" => "CARD",
            "SPLIT" => "SPLIT",
            "CASH" => "CASH",
            _ => normalized.ToUpperInvariant()
        };
    }
}

public sealed class ReceiptPrintResult
{
    public ReceiptPrintResult(
        FlowDocument document,
        BitmapSource? qrCodeImage,
        string fiscalSignature,
        string verificationUrl)
    {
        Document = document;
        QrCodeImage = qrCodeImage;
        FiscalSignature = fiscalSignature;
        VerificationUrl = verificationUrl;
    }

    public FlowDocument Document { get; }
    public BitmapSource? QrCodeImage { get; }
    public string FiscalSignature { get; }
    public string VerificationUrl { get; }
}
