namespace PointOfSale.Core.Pricing;

/// <summary>
/// Decimal-safe POS totals (no floating-point drift). Standard Malawi VAT rate for tests and configurable lines.
/// </summary>
public static class PosTaxCalculator
{
    public const decimal MalawiStandardVatRatePercent = 17.5m;

    public static decimal RoundMoney(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal CalculateNetAmount(decimal unitPrice, decimal quantity) =>
        RoundMoney(unitPrice * quantity);

    public static decimal CalculateVatAmount(decimal netAmount, decimal ratePercent) =>
        RoundMoney(netAmount * ratePercent / 100m);

    public static decimal CalculateLineTotal(decimal unitPrice, decimal quantity, decimal ratePercent)
    {
        var net = CalculateNetAmount(unitPrice, quantity);
        return net + CalculateVatAmount(net, ratePercent);
    }

    /// <summary>
    /// Applies a taxable-net discount, then recalculates VAT so totals stay aligned with 17.5% rounding.
    /// </summary>
    public static (decimal NetAfterDiscount, decimal Vat, decimal Gross, decimal DiscountApplied) ApplyNetDiscount(
        decimal unitPrice,
        decimal quantity,
        decimal ratePercent,
        decimal discountNet)
    {
        var grossNet = CalculateNetAmount(unitPrice, quantity);
        var discount = RoundMoney(Math.Min(Math.Max(0m, discountNet), grossNet));
        var net = RoundMoney(grossNet - discount);
        var vat = CalculateVatAmount(net, ratePercent);
        return (net, vat, net + vat, discount);
    }

    /// <summary>Maps unit price to invoice line net/VAT totals using the same rounding as checkout.</summary>
    public static (decimal Net, decimal Vat, decimal Gross) MapUnitPriceLine(
        decimal unitPrice,
        decimal quantity,
        decimal ratePercent)
    {
        var net = CalculateNetAmount(unitPrice, quantity);
        var vat = CalculateVatAmount(net, ratePercent);
        return (net, vat, net + vat);
    }
}
