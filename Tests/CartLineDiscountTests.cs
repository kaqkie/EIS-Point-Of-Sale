using PointOfSale.App.ViewModels;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using Xunit;

namespace PointOfSale.Tests;

public sealed class CartLineDiscountTests
{
    [Fact]
    public void ManualInclusiveDiscount_MatchesEisPortalExample()
    {
        // EIS: 20,000.00 shelf − 1,000.00 discount ⇒ taxable 16,170.21 / VAT 2,829.79 / total 19,000.00
        var line = CartLineViewModel.FromProduct(
            new LocalInventoryItem
            {
                ProductId = "990663831995",
                ProductCode = "990663831995",
                Name = "Air Cleaner SMA-230",
                UnitPrice = 20000m,
                StockQuantity = 1,
                TaxRateId = "A"
            },
            quantity: 1m);

        line.ManualDiscountInclusive = 1000m;
        line.RefreshTotals();

        Assert.Equal(1000m, line.DisplayedDiscountInclusive);
        Assert.Equal(19000m, line.LineTotal);
        Assert.Equal(
            PosTaxCalculator.ExtractExclusiveFromInclusive(19000m, PosTaxCalculator.MalawiStandardVatRatePercent),
            line.NetTotal);
        Assert.Equal(
            PosTaxCalculator.RoundMoney(19000m - line.NetTotal),
            line.VatTotal);
    }

    [Fact]
    public void ManualInclusiveDiscount_IsSentAsExclusiveNetOnInvoiceLine()
    {
        var line = CartLineViewModel.FromProduct(
            new LocalInventoryItem
            {
                ProductId = "1",
                ProductCode = "SKU-1",
                Name = "Item",
                UnitPrice = 1175m,
                StockQuantity = 1,
                TaxRateId = "A"
            },
            quantity: 1m);

        line.ManualDiscountInclusive = 117.5m;
        line.RefreshTotals();

        var invoice = line.ToInvoiceLine(1);
        Assert.Equal(line.TotalDiscountNet, invoice.Discount);
        Assert.Equal(line.NetTotal, invoice.Total);
        Assert.Equal(line.VatTotal, invoice.TotalVat);
        Assert.True(invoice.Discount > 0m);
    }
}
