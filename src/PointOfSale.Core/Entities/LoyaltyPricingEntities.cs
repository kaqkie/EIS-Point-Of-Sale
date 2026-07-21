namespace PointOfSale.Core.Entities;

public static class PricingRuleTypes
{
    public const string CategoryPercent = "CategoryPercent";
    public const string Bogo = "Bogo";
    public const string PromoPrice = "PromoPrice";

    public static readonly string[] All = [CategoryPercent, Bogo, PromoPrice];
}

public static class LoyaltyLedgerTypes
{
    public const string Earn = "Earn";
    public const string Redeem = "Redeem";
    public const string Adjust = "Adjust";
}

public sealed class LoyaltyMember
{
    public int MemberId { get; set; }
    public string MemberCode { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public decimal PointsBalance { get; set; }
    public decimal LifetimeSpendMwk { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastPurchaseAtUtc { get; set; }
}

public sealed class LoyaltyLedgerEntry
{
    public long LedgerId { get; set; }
    public int MemberId { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public decimal Points { get; set; }
    public decimal AmountMwk { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class PricingRule
{
    public int RuleId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RuleType { get; set; } = PricingRuleTypes.CategoryPercent;
    public string? CategoryCode { get; set; }
    public string? ProductCode { get; set; }
    public decimal PercentOff { get; set; }
    public decimal BuyQuantity { get; set; } = 1;
    public decimal FreeQuantity { get; set; } = 1;
    public decimal? PromoUnitPrice { get; set; }
    public DateTime StartsAtUtc { get; set; }
    public DateTime? EndsAtUtc { get; set; }
    public int Priority { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class PricingCartLine
{
    public required string ProductCode { get; init; }
    public required string Description { get; init; }
    public string? CategoryCode { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Quantity { get; init; }
    public decimal VatRatePercent { get; init; }
}
