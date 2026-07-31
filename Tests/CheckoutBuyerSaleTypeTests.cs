using PointOfSale.App.ViewModels;
using PointOfSale.Mra.Contracts.Utilities;
using Xunit;

namespace PointOfSale.Tests;

public class CheckoutBuyerSaleTypeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1234")]
    [InlineData("abcd")]
    [InlineData("1234567890123456")]
    public void NormalizeBuyerTin_rejects_invalid(string? value)
    {
        Assert.Null(CheckoutViewModel.NormalizeBuyerTin(value));
    }

    [Theory]
    [InlineData("20122074", "20122074")]
    [InlineData(" 10020030 ", "10020030")]
    [InlineData("TIN-9876543210", "9876543210")]
    public void NormalizeBuyerTin_accepts_digit_tins(string input, string expected)
    {
        Assert.Equal(expected, CheckoutViewModel.NormalizeBuyerTin(input));
    }
}

public class EisProductDescriptionMatchTests
{
    [Fact]
    public void ResolveName_prefers_description_and_keeps_internal_double_spaces()
    {
        var dto = new TerminalSiteProductDto
        {
            ProductCode = "303878224423",
            ProductName = "Air Cleaner For Car",
            Description = "Air Cleaner  13 780-S8JF01"
        };

        Assert.Equal("Air Cleaner  13 780-S8JF01", dto.ResolveName());
        Assert.Contains("  ", dto.ResolveName());
    }
}
