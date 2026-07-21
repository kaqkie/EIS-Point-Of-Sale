namespace PointOfSale.App.Options;

public sealed class LoyaltyProgramOptions
{
    public const string SectionName = "LoyaltyProgram";

    /// <summary>Points earned per 1,000 MWK of invoice total (after discounts).</summary>
    public decimal PointsPerThousandMwk { get; set; } = 1m;

    /// <summary>MWK value of one redeemed point (tender discount against taxable net).</summary>
    public decimal MwkPerRedeemedPoint { get; set; } = 1m;

    /// <summary>Minimum redeemable points in one checkout.</summary>
    public decimal MinimumRedeemPoints { get; set; } = 10m;

    public bool Enabled { get; set; } = true;
}
