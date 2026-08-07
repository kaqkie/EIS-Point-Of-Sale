namespace PointOfSale.Core.Pricing;

/// <summary>
/// Decimal-safe POS totals (no floating-point drift). Standard Malawi VAT rate for tests and configurable lines.
/// Inventory <c>UnitPrice</c> is the shelf / VAT-inclusive amount (matches MRA EIS receipt math).
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
    /// Backs exclusive (taxable) amount out of a VAT-inclusive amount:
    /// <c>inclusive ÷ (1 + rate/100)</c>, AwayFromZero to 2 dp — same as MRA EIS receipts.
    /// </summary>
    public static decimal ExtractExclusiveFromInclusive(decimal inclusiveAmount, decimal ratePercent)
    {
        var inclusive = RoundMoney(Math.Max(0m, inclusiveAmount));
        if (ratePercent <= 0m)
        {
            return inclusive;
        }

        return RoundMoney(inclusive / (1m + ratePercent / 100m));
    }

    /// <summary>Exclusive unit price from a VAT-inclusive shelf unit price.</summary>
    public static decimal ExtractExclusiveUnitFromInclusive(decimal inclusiveUnitPrice, decimal ratePercent) =>
        ExtractExclusiveFromInclusive(inclusiveUnitPrice, ratePercent);

    /// <summary>
    /// Maps a VAT-inclusive inventory unit price to exclusive net / VAT / gross line totals.
    /// Gross stays the shelf total; VAT = gross − net (avoids 1-cent drift vs rate×net).
    /// </summary>
    public static (decimal Net, decimal Vat, decimal Gross) MapInclusiveUnitPriceLine(
        decimal inclusiveUnitPrice,
        decimal quantity,
        decimal ratePercent)
    {
        var gross = CalculateNetAmount(inclusiveUnitPrice, quantity);
        if (ratePercent <= 0m)
        {
            return (gross, 0m, gross);
        }

        var net = ExtractExclusiveFromInclusive(gross, ratePercent);
        var vat = RoundMoney(gross - net);
        return (net, vat, gross);
    }

    /// <summary>
    /// Applies a taxable-net discount to a VAT-inclusive shelf unit price, then recalculates VAT.
    /// </summary>
    public static (decimal NetAfterDiscount, decimal Vat, decimal Gross, decimal DiscountApplied) ApplyInclusiveDiscount(
        decimal inclusiveUnitPrice,
        decimal quantity,
        decimal ratePercent,
        decimal discountNet)
    {
        var (net, _, _) = MapInclusiveUnitPriceLine(inclusiveUnitPrice, quantity, ratePercent);
        var discount = RoundMoney(Math.Min(Math.Max(0m, discountNet), net));
        var netAfter = RoundMoney(net - discount);
        var vat = ratePercent <= 0m ? 0m : CalculateVatAmount(netAfter, ratePercent);
        return (netAfter, vat, netAfter + vat, discount);
    }

    /// <summary>
    /// Applies a taxable-net discount, then recalculates VAT so totals stay aligned with rate rounding.
    /// Unit price is exclusive (taxable) — prefer <see cref="ApplyInclusiveDiscount"/> for inventory shelf prices.
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

    /// <summary>
    /// Maps exclusive unit price to invoice line net/VAT totals (legacy / harness helpers).
    /// Inventory shelf prices should use <see cref="MapInclusiveUnitPriceLine"/>.
    /// </summary>
    public static (decimal Net, decimal Vat, decimal Gross) MapUnitPriceLine(
        decimal unitPrice,
        decimal quantity,
        decimal ratePercent)
    {
        var net = CalculateNetAmount(unitPrice, quantity);
        var vat = CalculateVatAmount(net, ratePercent);
        return (net, vat, net + vat);
    }

    /// <summary>
    /// For VAT relief (<c>isReliefSupply</c>) sales: keep taxable net, remove standard VAT.
    /// Non-standard / exempt rates are left unchanged. Unit price is exclusive.
    /// </summary>
    public static (decimal Net, decimal Vat, decimal Gross) ApplyReliefSupplyLine(
        decimal unitPrice,
        decimal quantity,
        decimal ratePercent,
        bool isStandardVatTier)
    {
        var net = CalculateNetAmount(unitPrice, quantity);
        if (!isStandardVatTier || ratePercent <= 0m)
        {
            var vat = CalculateVatAmount(net, ratePercent);
            return (net, vat, net + vat);
        }

        return (net, 0m, net);
    }
}
