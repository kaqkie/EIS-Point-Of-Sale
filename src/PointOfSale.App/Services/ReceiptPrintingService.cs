using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PointOfSale.Mra.Contracts.Sales;
using QRCoder;

namespace PointOfSale.App.Services;

public interface IReceiptPrintingService
{
    ReceiptPrintResult BuildReceipt(ReceiptPrintRequest request);
    Task PrintAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default);
}

public sealed class ReceiptPrintingService : IReceiptPrintingService
{
    private const double ThermalWidth = 280;

    public ReceiptPrintResult BuildReceipt(ReceiptPrintRequest request)
    {
        var fiscalSignature = request.FiscalResponse?.ResolveFiscalSignature() ?? string.Empty;
        var verificationUrl = request.FiscalResponse?.VerificationUrl ?? string.Empty;
        var qr = CreateQrImage(verificationUrl);

        var document = new FlowDocument
        {
            PageWidth = ThermalWidth,
            PagePadding = new Thickness(8),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11
        };

        document.Blocks.Add(Heading(request.TradingName));
        foreach (var line in request.AddressLines)
        {
            document.Blocks.Add(Line(line));
        }

        document.Blocks.Add(Line($"TIN: {request.SellerTin}"));
        document.Blocks.Add(Line($"Invoice: {request.InvoiceNumber}"));
        document.Blocks.Add(Line($"Date: {request.InvoiceDateTime:yyyy-MM-dd HH:mm}"));
        document.Blocks.Add(Spacer());

        foreach (var item in request.LineItems)
        {
            document.Blocks.Add(Line($"{item.Description}"));
            document.Blocks.Add(Line($"  {item.Quantity:N2} x {item.UnitPrice:N2} = {item.Total:N2}  VAT {item.TotalVat:N2}"));
        }

        document.Blocks.Add(Spacer());
        foreach (var tax in request.TaxBreakdown)
        {
            document.Blocks.Add(Line($"Tax {tax.RateId}: {tax.TaxableAmount:N2} + {tax.TaxAmount:N2}"));
        }

        document.Blocks.Add(Line($"TOTAL: {request.InvoiceTotal:N2}"));
        document.Blocks.Add(Line($"Tendered: {request.AmountTendered:N2}"));
        document.Blocks.Add(Spacer());
        document.Blocks.Add(Line("MRA EIS Fiscal Signature:"));
        document.Blocks.Add(Line(Chunk(fiscalSignature, 32)));
        if (!string.IsNullOrWhiteSpace(verificationUrl))
        {
            document.Blocks.Add(Line("Verify:"));
            document.Blocks.Add(Line(Chunk(verificationUrl, 32)));
            document.Blocks.Add(CreateQrBlock(qr));
        }

        document.Blocks.Add(Line("Thank you — Albert Retail Terminal"));

        return new ReceiptPrintResult(document, qr, fiscalSignature, verificationUrl);
    }

    public Task PrintAsync(ReceiptPrintRequest request, CancellationToken cancellationToken = default)
    {
        var receipt = BuildReceipt(request);
        var printDialog = new PrintDialog();
        if (printDialog.PrintQueue is null)
        {
            printDialog.PrintQueue = LocalPrintServer.GetDefaultPrintQueue();
        }

        if (printDialog.PrintQueue is null)
        {
            throw new InvalidOperationException("No default printer is configured for thermal receipt printing.");
        }

        receipt.Document.PageHeight = double.MaxValue;
        printDialog.PrintDocument(((IDocumentPaginatorSource)receipt.Document).DocumentPaginator, "Albert Retail Terminal Receipt");
        return Task.CompletedTask;
    }

    private static BitmapSource? CreateQrImage(string verificationUrl)
    {
        if (string.IsNullOrWhiteSpace(verificationUrl))
        {
            return null;
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(verificationUrl, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        var bytes = png.GetGraphic(4);

        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BlockUIContainer CreateQrBlock(BitmapSource? qr)
    {
        var container = new BlockUIContainer();
        if (qr is null)
        {
            return container;
        }

        var image = new Image
        {
            Source = qr,
            Width = 120,
            Height = 120,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        container.Child = image;
        return container;
    }

    private static Paragraph Heading(string text) =>
        new(new Run(text) { FontWeight = FontWeights.Bold, FontSize = 14 })
        {
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };

    private static Paragraph Line(string text) =>
        new(new Run(text)) { Margin = new Thickness(0, 0, 0, 2) };

    private static Paragraph Spacer() => new(new Run(" ")) { Margin = new Thickness(0, 4, 0, 4) };

    private static string Chunk(string value, int size)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        for (var i = 0; i < value.Length; i += size)
        {
            parts.Add(value.Substring(i, Math.Min(size, value.Length - i)));
        }

        return string.Join(Environment.NewLine, parts);
    }
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
    public SubmitSalesTransactionResponseData? FiscalResponse { get; init; }
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
