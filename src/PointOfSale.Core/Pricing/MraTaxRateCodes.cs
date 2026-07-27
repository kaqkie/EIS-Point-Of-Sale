namespace PointOfSale.Core.Pricing;

/// <summary>
/// MRA EIS tax rate lookup codes. Standard VAT tier is <see cref="StandardVat"/> (<c>STANDARD_17_5</c>).
/// </summary>
public static class MraTaxRateCodes
{
    /// <summary>MRA lookup code for the standard 17.5% VAT tier.</summary>
    public const string StandardVat = "STANDARD_17_5";

    /// <summary>Legacy ART / sandbox alias that must be remapped before EIS submit.</summary>
    public const string LegacyStandardAlias = "T";

    /// <summary>Historical single-letter MRA code for the standard VAT tier.</summary>
    public const string LegacyStandardAliasA = "A";

    public const string Exempt = "E";

    /// <summary>True when the id represents the standard VAT tier (17.5%).</summary>
    public static bool IsStandardVatTier(string? taxRateId)
    {
        if (string.IsNullOrWhiteSpace(taxRateId))
        {
            return true;
        }

        var id = taxRateId.Trim();
        return id.Equals(StandardVat, StringComparison.OrdinalIgnoreCase)
               || id.Equals(LegacyStandardAliasA, StringComparison.OrdinalIgnoreCase)
               || id.Equals(LegacyStandardAlias, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps empty / legacy identifiers onto <see cref="StandardVat"/>.
    /// Known non-standard MRA codes (E, Z, TL, …) are preserved as-is.
    /// </summary>
    public static string Normalize(string? taxRateId)
    {
        if (IsStandardVatTier(taxRateId))
        {
            return StandardVat;
        }

        return taxRateId!.Trim();
    }

    /// <summary>
    /// Resolves the percentage for a tax rate id from MRA global config rates when available.
    /// Falls back to Malawi statutory 17.5% for standard VAT codes.
    /// </summary>
    public static decimal ResolveRatePercent(
        string? taxRateId,
        IEnumerable<(string Id, decimal Rate)>? configuredRates)
    {
        if (IsStandardVatTier(taxRateId))
        {
            return PosTaxCalculator.MalawiStandardVatRatePercent;
        }

        var id = taxRateId?.Trim() ?? string.Empty;

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

    /// <summary>
    /// Picks the activated standard VAT rate id from global config / taxpayer activation.
    /// </summary>
    public static string ResolveStandardRateId(
        IEnumerable<(string Id, decimal Rate)>? configuredRates,
        IEnumerable<string>? activatedTaxRateIds)
    {
        if (configuredRates is not null)
        {
            var exact = configuredRates
                .FirstOrDefault(r =>
                    !string.IsNullOrWhiteSpace(r.Id)
                    && r.Rate == PosTaxCalculator.MalawiStandardVatRatePercent);
            if (!string.IsNullOrWhiteSpace(exact.Id))
            {
                return exact.Id.Trim();
            }

            var band = configuredRates
                .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Id) && r.Rate is >= 16m and <= 18m);
            if (!string.IsNullOrWhiteSpace(band.Id))
            {
                return band.Id.Trim();
            }
        }

        if (activatedTaxRateIds is not null)
        {
            foreach (var id in activatedTaxRateIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var trimmed = id.Trim();
                if (trimmed.Equals(StandardVat, StringComparison.OrdinalIgnoreCase)
                    || trimmed.Equals(LegacyStandardAliasA, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed;
                }
            }
        }

        return StandardVat;
    }
}
