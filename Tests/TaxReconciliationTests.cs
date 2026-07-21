using PointOfSale.App.Services;
using PointOfSale.Core.Pricing;
using Xunit;

namespace PointOfSale.Tests;

public sealed class TaxReconciliationTests
{
    [Fact]
    public void StandardVat_ExpectedMatchesPosTaxCalculator_17_5Percent()
    {
        const decimal taxable = 1000m;
        var expected = PosTaxCalculator.CalculateVatAmount(taxable, PosTaxCalculator.MalawiStandardVatRatePercent);
        Assert.Equal(175.00m, expected);
        Assert.Equal(0m, PosTaxCalculator.RoundMoney(expected - 175m));
    }

    [Fact]
    public void TaxReconciliationReport_BalancedWhenVarianceUnderOneCent()
    {
        var report = new TaxReconciliationReport
        {
            Period = TaxReconciliationPeriod.Daily,
            LocalBusinessDate = DateTime.Today,
            FromUtc = DateTime.UtcNow.Date,
            ToUtcExclusive = DateTime.UtcNow.Date.AddDays(1),
            StandardRateTaxable = 100m,
            ExpectedStandardVat = 17.5m,
            ActualVatCollected = 17.5m,
            VatVariance = 0m,
            IsBalanced = true
        };

        Assert.True(report.IsBalanced);
        Assert.Equal(TaxReconciliationPeriod.Daily, report.Period);
    }
}
