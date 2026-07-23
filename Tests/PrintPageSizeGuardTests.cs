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
}
