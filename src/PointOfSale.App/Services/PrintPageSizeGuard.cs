using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace PointOfSale.App.Services;

/// <summary>
/// Guards FlowDocument page dimensions against Double.MaxValue / Infinity / NaN,
/// which WPF rejects when assigning <see cref="FlowDocument.PageHeight"/> / PageWidth.
/// </summary>
public static class PrintPageSizeGuard
{
    /// <summary>80mm thermal at 96 DPI ≈ 302 DIPs; keep slightly under for margins.</summary>
    public const double DefaultThermalWidth80Dip = 280;

    /// <summary>58mm thermal at 96 DPI ≈ 219 DIPs.</summary>
    public const double DefaultThermalWidth58Dip = 200;

    /// <summary>Finite continuous-roll height fallback (~21&quot; at 96 DPI).</summary>
    public const double DefaultReceiptHeightDip = 2000;

    public const double MinPageDimensionDip = 72;       // 1 inch
    public const double MaxPageWidthDip = 1200;         // wide label stock upper bound
    public const double MaxPageHeightDip = 20_000;      // long receipt / multi-label upper bound
    private const double UnreasonableThresholdDip = 100_000;

    public static double ResolveThermalWidthDip(int paperWidthMm) =>
        paperWidthMm <= 58 ? DefaultThermalWidth58Dip : DefaultThermalWidth80Dip;

    public static void ApplySafePageSize(
        FlowDocument document,
        PrintDialog? printDialog,
        double fallbackWidthDip,
        double fallbackHeightDip = DefaultReceiptHeightDip)
    {
        ArgumentNullException.ThrowIfNull(document);

        var width = Sanitize(
            printDialog?.PrintableAreaWidth ?? double.NaN,
            fallbackWidthDip,
            MinPageDimensionDip,
            MaxPageWidthDip);

        var height = Sanitize(
            printDialog?.PrintableAreaHeight ?? double.NaN,
            fallbackHeightDip,
            MinPageDimensionDip,
            MaxPageHeightDip);

        // Prefer a content-fitted height when we can measure without binding to a visual tree permanently.
        var measured = TryMeasureContentHeight(document, width);
        if (measured is > 0)
        {
            height = Math.Clamp(measured.Value + document.PagePadding.Top + document.PagePadding.Bottom + 24,
                MinPageDimensionDip,
                MaxPageHeightDip);
        }

        document.PageWidth = width;
        document.PageHeight = height;
        document.ColumnWidth = Math.Max(
            MinPageDimensionDip,
            width - document.PagePadding.Left - document.PagePadding.Right);
    }

    /// <summary>
    /// Receipt / PDF print sizing: keep an 80mm/58mm thermal column and content-fitted height.
    /// Never expand to letter/A4 printable area — that centers the MRA QR in the middle of a blank PDF page.
    /// </summary>
    public static void ApplyThermalReceiptPageSize(
        FlowDocument document,
        PrintDialog? printDialog,
        double thermalWidthDip,
        double estimatedHeightDip)
    {
        ArgumentNullException.ThrowIfNull(document);

        var thermalWidth = Sanitize(
            thermalWidthDip,
            DefaultThermalWidth80Dip,
            MinPageDimensionDip,
            MaxPageWidthDip);

        // Cap at printable width only when the target printer is narrower than thermal stock.
        var printableWidth = printDialog?.PrintableAreaWidth ?? double.NaN;
        var width = thermalWidth;
        if (IsValidPageDimension(printableWidth)
            && printableWidth >= MinPageDimensionDip
            && printableWidth < thermalWidth)
        {
            width = printableWidth;
        }

        var measured = TryMeasureContentHeight(document, width);
        var height = measured is > 0
            ? Math.Clamp(
                measured.Value + document.PagePadding.Top + document.PagePadding.Bottom + 32,
                MinPageDimensionDip,
                MaxPageHeightDip)
            : Sanitize(
                estimatedHeightDip,
                DefaultReceiptHeightDip,
                MinPageDimensionDip,
                MaxPageHeightDip);

        // Never stretch height to full A4/Letter — leave content tightly packed so the QR sits under totals.
        document.PageWidth = width;
        document.PageHeight = height;
        document.ColumnWidth = Math.Max(
            MinPageDimensionDip,
            width - document.PagePadding.Left - document.PagePadding.Right);
    }

    public static void EnsureDocumentReadyToPrint(FlowDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!IsValidPageDimension(document.PageWidth) || !IsValidPageDimension(document.PageHeight))
        {
            throw new InvalidOperationException(
                $"Receipt page size is invalid (Width={document.PageWidth}, Height={document.PageHeight}). " +
                "Configure ThermalPrinter:PaperWidthMm and ensure a printer with finite printable area is selected.");
        }
    }

    public static double Sanitize(double candidate, double fallback, double min, double max)
    {
        if (!IsValidPageDimension(candidate) || candidate < min || candidate > UnreasonableThresholdDip)
        {
            return Math.Clamp(fallback, min, max);
        }

        return Math.Clamp(candidate, min, max);
    }

    public static bool IsValidPageDimension(double value) =>
        !double.IsNaN(value)
        && !double.IsInfinity(value)
        && value > 0
        && value < UnreasonableThresholdDip
        && value != double.MaxValue
        && value != double.MinValue;

    private static double? TryMeasureContentHeight(FlowDocument document, double pageWidth)
    {
        try
        {
            document.PageWidth = pageWidth;
            document.ColumnWidth = Math.Max(
                MinPageDimensionDip,
                pageWidth - document.PagePadding.Left - document.PagePadding.Right);

            // Temporary tall finite page so the paginator lays out as a single continuous strip.
            var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
            paginator.PageSize = new Size(pageWidth, MaxPageHeightDip);
            _ = paginator.PageCount;
            if (paginator.PageCount <= 0)
            {
                return null;
            }

            // Approximate: one logical page of MaxPageHeight means content may be shorter;
            // use DesiredSize via a detached viewer when possible.
            var viewer = new FlowDocumentScrollViewer
            {
                Document = document,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden
            };
            viewer.Measure(new Size(pageWidth, MaxPageHeightDip));
            viewer.Arrange(new Rect(0, 0, pageWidth, Math.Max(viewer.DesiredSize.Height, MinPageDimensionDip)));
            var height = viewer.DesiredSize.Height;
            viewer.Document = null;
            return height > 0 ? height : null;
        }
        catch
        {
            return null;
        }
    }
}
