using System.Windows;
using System.Windows.Documents;
using PointOfSale.App.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class PrintPageSizeGuardTests
{
    [Fact]
    public void Sanitize_RejectsDoubleMaxValue_AndInfinity()
    {
        Assert.Equal(
            280,
            PrintPageSizeGuard.Sanitize(double.MaxValue, fallback: 280, min: 72, max: 1200));
        Assert.Equal(
            280,
            PrintPageSizeGuard.Sanitize(double.PositiveInfinity, fallback: 280, min: 72, max: 1200));
        Assert.Equal(
            280,
            PrintPageSizeGuard.Sanitize(double.NaN, fallback: 280, min: 72, max: 1200));
        Assert.Equal(
            280,
            PrintPageSizeGuard.Sanitize(0, fallback: 280, min: 72, max: 1200));
        Assert.Equal(
            400,
            PrintPageSizeGuard.Sanitize(400, fallback: 280, min: 72, max: 1200));
    }

    [Fact]
    public void IsValidPageDimension_RejectsMaxValue()
    {
        Assert.False(PrintPageSizeGuard.IsValidPageDimension(double.MaxValue));
        Assert.False(PrintPageSizeGuard.IsValidPageDimension(double.PositiveInfinity));
        Assert.True(PrintPageSizeGuard.IsValidPageDimension(280));
        Assert.True(PrintPageSizeGuard.IsValidPageDimension(2000));
    }

    [Fact]
    public void ResolveThermalWidthDip_MatchesPaperStock()
    {
        Assert.Equal(PrintPageSizeGuard.DefaultThermalWidth58Dip, PrintPageSizeGuard.ResolveThermalWidthDip(58));
        Assert.Equal(PrintPageSizeGuard.DefaultThermalWidth80Dip, PrintPageSizeGuard.ResolveThermalWidthDip(80));
    }

    [Fact]
    public void ApplyThermalReceiptPageSize_KeepsThermalWidthOnPdfTarget()
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(8),
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 11
        };
        for (var i = 0; i < 20; i++)
        {
            document.Blocks.Add(new Paragraph(new Run($"LINE {i}")) { Margin = new Thickness(0) });
        }

        // Must keep ~80mm width and content-fitted height (not letter/A4), or the QR lands mid-page.
        PrintPageSizeGuard.ApplyThermalReceiptPageSize(
            document,
            printDialog: null,
            thermalWidthDip: PrintPageSizeGuard.DefaultThermalWidth80Dip,
            estimatedHeightDip: 640);

        Assert.Equal(PrintPageSizeGuard.DefaultThermalWidth80Dip, document.PageWidth);
        Assert.True(document.PageHeight < 1200, $"Expected content-fitted height, got {document.PageHeight}");
        Assert.True(document.PageHeight >= PrintPageSizeGuard.MinPageDimensionDip);
        PrintPageSizeGuard.EnsureDocumentReadyToPrint(document);
    }
}
