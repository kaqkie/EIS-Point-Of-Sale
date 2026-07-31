using PointOfSale.App.ViewModels;
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
