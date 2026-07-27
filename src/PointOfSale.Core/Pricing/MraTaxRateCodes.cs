namespace PointOfSale.Core.Pricing;

/// <summary>
/// MRA EIS tax rate lookup codes. Standard VAT tier is <see cref="StandardVat"/> (<c>A</c>).
/// </summary>
public static class MraTaxRateCodes
{
    /// <summary>MRA lookup code for the standard VAT tier (historically 16.5% / 17.5% per global config).</summary>
    public const string StandardVat = "A";

    /// <summary>Legacy ART default that must be remapped to <see cref="StandardVat"/> before EIS submit.</summary>
    public const string LegacyStandardAlias = "T";

    public const string Exempt = "E";

    /// <summary>
    /// Maps empty / legacy <c>T</c> identifiers onto <see cref="StandardVat"/> (<c>A</c>).
    /// Known MRA codes (A, E, TL, …) are preserved as-is.
    /// </summary>
    public static string Normalize(string? taxRateId)
    {
        if (string.IsNullOrWhiteSpace(taxRateId))
        {
            return StandardVat;
        }

        var trimmed = taxRateId.Trim();
        if (trimmed.Equals(LegacyStandardAlias, StringComparison.OrdinalIgnoreCase))
        {
            return StandardVat;
        }

        return trimmed;
    }

    /// <summary>
    /// Resolves the percentage for a tax rate id from MRA global config rates when available.
    /// Falls back to Malawi statutory 17.5% for standard VAT codes.
    /// </summary>
    public static decimal ResolveRatePercent(
        string? taxRateId,
        IEnumerable<(string Id, decimal Rate)>? configuredRates)
    {
        var id = Normalize(taxRateId);

        // Requirement: standard VAT tier must be represented as exactly 17.5%
        // (even if the sandbox/global config cache is stale or uses a different
        // rounding band). This ensures invoice VAT math + payload always stays aligned.
        if (id.Equals(StandardVat, StringComparison.OrdinalIgnoreCase))
        {
            return PosTaxCalculator.MalawiStandardVatRatePercent;
        }

        if (configuredRates is not null)
        {
            foreach (var rate in configuredRates)
            {
                if (string.IsNullOrWhiteSpace(rate.Id))
                {
                    continue;
                }

                if (rate.Id.Trim().Equals(id, StringComparison.OrdinalIgnoreCase) && rate.Rate > 0m)
                {
                    return rate.Rate;
                }
            }
        }

        if (id.Equals(Exempt, StringComparison.OrdinalIgnoreCase) ||
            id.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            return 0m;
        }

        return PosTaxCalculator.MalawiStandardVatRatePercent;
    }
}
