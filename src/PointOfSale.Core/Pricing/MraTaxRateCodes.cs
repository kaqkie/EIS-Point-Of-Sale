namespace PointOfSale.Core.Pricing;

/// <summary>
/// MRA EIS tax rate lookup codes. Standard VAT tier uses <see cref="StandardVat"/> (<c>A</c>),
/// matching <c>get-latest-configs</c> sample <c>taxrates[].id</c> / <c>activatedTaxRateIds</c>.
/// </summary>
public static class MraTaxRateCodes
{
    /// <summary>Official MRA lookup code for the standard VAT tier (sample config id <c>A</c>).</summary>
    public const string StandardVat = "A";

    /// <summary>Legacy ART alias that must be remapped before EIS submit.</summary>
    public const string LegacyStandardAlias = "T";

    /// <summary>Historical ART invention — not an MRA rate id; remap onto <see cref="StandardVat"/>.</summary>
    public const string LegacyStandardAliasStandard17_5 = "STANDARD_17_5";

    public const string Exempt = "E";

    /// <summary>True when the id represents the standard VAT tier.</summary>
    public static bool IsStandardVatTier(string? taxRateId)
    {
        if (string.IsNullOrWhiteSpace(taxRateId))
        {
            return true;
        }

        var id = taxRateId.Trim();
        return id.Equals(StandardVat, StringComparison.OrdinalIgnoreCase)
               || id.Equals(LegacyStandardAlias, StringComparison.OrdinalIgnoreCase)
               || id.Equals(LegacyStandardAliasStandard17_5, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps empty / legacy identifiers onto <see cref="StandardVat"/> (<c>A</c>).
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
    /// Falls back to Malawi statutory 17.5% for standard VAT codes when config is missing.
    /// </summary>
    public static decimal ResolveRatePercent(
        string? taxRateId,
        IEnumerable<(string Id, decimal Rate)>? configuredRates)
    {
        var id = taxRateId?.Trim() ?? string.Empty;
        var rates = configuredRates?
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && r.Rate > 0m)
            .Select(r => (Id: r.Id.Trim(), r.Rate))
            .ToList();

        if (rates is { Count: > 0 })
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                foreach (var rate in rates)
                {
                    if (rate.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                    {
                        return rate.Rate;
                    }
                }
            }

            if (IsStandardVatTier(id))
            {
                // Prefer the activated/standard id's configured rate (MRA sample: A @ 16.5).
                foreach (var rate in rates)
                {
                    if (IsStandardVatTier(rate.Id) || rate.Rate is >= 16m and <= 18m)
                    {
                        // Exact standard aliases first.
                        if (rate.Id.Equals(StandardVat, StringComparison.OrdinalIgnoreCase)
                            || rate.Id.Equals(LegacyStandardAliasStandard17_5, StringComparison.OrdinalIgnoreCase))
                        {
                            return rate.Rate;
                        }
                    }
                }

                var band = rates.FirstOrDefault(r => r.Rate is >= 16m and <= 18m);
                if (band.Rate > 0m)
                {
                    return band.Rate;
                }
            }
        }

        if (IsStandardVatTier(id))
        {
            return PosTaxCalculator.MalawiStandardVatRatePercent;
        }

        if (id.Equals(Exempt, StringComparison.OrdinalIgnoreCase) ||
            id.Equals("Z", StringComparison.OrdinalIgnoreCase))
        {
            return 0m;
        }

        return PosTaxCalculator.MalawiStandardVatRatePercent;
    }

    /// <summary>
    /// Picks the activated standard VAT rate id from taxpayer activation / global config.
    /// Prefers activated ids that exist in global taxrates (MRA sample: <c>A</c>).
    /// </summary>
    public static string ResolveStandardRateId(
        IEnumerable<(string Id, decimal Rate)>? configuredRates,
        IEnumerable<string>? activatedTaxRateIds)
    {
        var rates = configuredRates?
            .Where(r => !string.IsNullOrWhiteSpace(r.Id) && r.Rate > 0m)
            .Select(r => (Id: r.Id.Trim(), r.Rate))
            .ToList() ?? [];

        // 1) Prefer activated ids that appear in global taxrates (exact MRA identity).
        if (activatedTaxRateIds is not null)
        {
            foreach (var activated in activatedTaxRateIds)
            {
                if (string.IsNullOrWhiteSpace(activated))
                {
                    continue;
                }

                var trimmed = activated.Trim();
                var configured = rates.FirstOrDefault(r =>
                    r.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(configured.Id)
                    && (IsStandardVatTier(configured.Id) || configured.Rate is >= 16m and <= 18m))
                {
                    return Normalize(configured.Id);
                }

                // Activated "A" wins over a locally seeded STANDARD_17_5 row.
                if (IsStandardVatTier(trimmed)
                    && rates.Any(r => IsStandardVatTier(r.Id) || r.Rate is >= 16m and <= 18m))
                {
                    return Normalize(trimmed);
                }
            }
        }

        // 2) Rate-based match in global taxrates.
        if (rates.Count > 0)
        {
            var exact = rates.FirstOrDefault(r =>
                r.Rate == PosTaxCalculator.MalawiStandardVatRatePercent);
            if (!string.IsNullOrWhiteSpace(exact.Id))
            {
                return Normalize(exact.Id);
            }

            var band = rates.FirstOrDefault(r => r.Rate is >= 16m and <= 18m);
            if (!string.IsNullOrWhiteSpace(band.Id))
            {
                return Normalize(band.Id);
            }
        }

        // 3) Activated standard-tier aliases even without a matching global row.
        if (activatedTaxRateIds is not null)
        {
            foreach (var id in activatedTaxRateIds)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var trimmed = id.Trim();
                if (IsStandardVatTier(trimmed))
                {
                    return Normalize(trimmed);
                }
            }
        }

        return StandardVat;
    }
}
