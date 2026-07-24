using PointOfSale.App.ViewModels;
using Xunit;

namespace PointOfSale.Tests;

public sealed class PaymentMethodCommandTests
{
    [Theory]
    [InlineData("Cash", "Cash")]
    [InlineData("cash", "Cash")]
    [InlineData("Credit", "Card")]
    [InlineData("Other Card", "Card")]
    [InlineData("Card", "Card")]
    [InlineData("Gift Card", "MobileMoney")]
    [InlineData("MobileMoney", "MobileMoney")]
    public void NormalizePaymentMethod_MapsRegisterButtons(string input, string expected)
    {
        Assert.Equal(expected, CheckoutViewModel.NormalizePaymentMethodForTest(input));
    }
}
