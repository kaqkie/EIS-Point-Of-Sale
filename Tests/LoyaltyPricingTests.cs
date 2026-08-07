using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using Xunit;

namespace PointOfSale.Tests;

public sealed class LoyaltyPricingTests
{
    [Fact]
    public void ApplyNetDiscount_KeepsVatAlignedTo17_5()
    {
        var (net, vat, gross, discount) = PosTaxCalculator.ApplyNetDiscount(
            unitPrice: 1000m,
            quantity: 1m,
            ratePercent: 17.5m,
            discountNet: 100m);

        Assert.Equal(100m, discount);
        Assert.Equal(900m, net);
        Assert.Equal(157.50m, vat);
        Assert.Equal(1057.50m, gross);
    }

    [Fact]
    public void PricingRulesEngine_AppliesHigherPriorityPromoPriceOverPercent()
    {
        var engine = new PricingRulesEngine(null!);
        var lines = new[]
        {
            new PricingCartLine
            {
                ProductCode = "SKU-1",
                Description = "Item",
                CategoryCode = "T",
                UnitPrice = 1000m,
                Quantity = 1,
                VatRatePercent = 17.5m
            }
        };

        var rules = new[]
        {
            new PricingRule
            {
                RuleId = 1,
                Name = "10% category",
                RuleType = PricingRuleTypes.CategoryPercent,
                CategoryCode = "T",
                PercentOff = 10m,
                Priority = 10,
                IsActive = true,
                StartsAtUtc = DateTime.UtcNow.AddDays(-1)
            },
            new PricingRule
            {
                RuleId = 2,
                Name = "Promo 800",
                RuleType = PricingRuleTypes.PromoPrice,
                ProductCode = "SKU-1",
                PromoUnitPrice = 800m,
                Priority = 50,
                IsActive = true,
                StartsAtUtc = DateTime.UtcNow.AddDays(-1)
            }
        };

        var result = engine.Evaluate(lines, rules);
        Assert.Single(result.LineAdjustments);
        // Inclusive 1000 → exclusive 851.06; promo 800 → 680.85; discount = 170.21
        Assert.Equal(170.21m, result.LineAdjustments[0].DiscountNet);
        Assert.Equal("Promo 800", result.LineAdjustments[0].AppliedRuleName);
    }

    [Fact]
    public void PricingRulesEngine_BogoGivesFreeUnits()
    {
        var engine = new PricingRulesEngine(null!);
        var lines = new[]
        {
            new PricingCartLine
            {
                ProductCode = "BOGO-1",
                Description = "Drink",
                CategoryCode = "T",
                UnitPrice = 500m,
                Quantity = 3,
                VatRatePercent = 17.5m
            }
        };

        var rules = new[]
        {
            new PricingRule
            {
                RuleId = 3,
                Name = "Buy2Get1",
                RuleType = PricingRuleTypes.Bogo,
                ProductCode = "BOGO-1",
                BuyQuantity = 2,
                FreeQuantity = 1,
                Priority = 1,
                IsActive = true,
                StartsAtUtc = DateTime.UtcNow.AddDays(-1)
            }
        };

        var result = engine.Evaluate(lines, rules);
        // One free unit at inclusive 500 → exclusive 425.53
        Assert.Equal(425.53m, result.TotalDiscountNet);
    }

    [Fact]
    public void LoyaltyEarn_PointsPerThousand()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PointOfSale.App.Options.LoyaltyProgramOptions
        {
            Enabled = true,
            PointsPerThousandMwk = 1m
        });
        var service = new LoyaltyProgramService(null!, options);
        Assert.Equal(2.50m, service.CalculateEarnPoints(2500m));
        Assert.Equal(25m, service.CalculateRedeemValueMwk(25m));
    }
}
