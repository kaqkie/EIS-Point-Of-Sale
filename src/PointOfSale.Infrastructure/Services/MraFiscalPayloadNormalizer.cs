using System.Text.RegularExpressions;
using PointOfSale.Core.Pricing;
using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.Infrastructure.Services;

/// <summary>
/// Aligns queued / checkout sales payloads with MRA EIS field expectations
/// (site codes, taxRateId <c>A</c>, config versions, 2-dp money).
/// </summary>
public static partial class MraFiscalPayloadNormalizer
{
    public static SubmitSalesTransactionRequest Normalize(
        SubmitSalesTransactionRequest request,
        MraFiscalIdentityOverlay? identity = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sellerTin = FirstNonEmpty(identity?.SellerTin, request.InvoiceHeader.SellerTin)?.Trim() ?? string.Empty;
        var siteId = NormalizeSiteId(FirstNonEmpty(identity?.SiteId, request.InvoiceHeader.SiteId));

        // When overlay is absent (final wire hop), preserve an already-resolved standard-tier
        // id from the payload instead of inventing STANDARD_17_5 over MRA's activated "A".
        var standardTaxRateId = ResolveOverlayStandardTaxRateId(identity, request);

        var configuredRates = identity?.ConfiguredTaxRates;

        var header = new InvoiceHeaderDto
        {
            InvoiceNumber = request.InvoiceHeader.InvoiceNumber.Trim(),
            InvoiceDateTime = OfflineSalesQueueService.NormalizeInvoiceDateTime(request.InvoiceHeader.InvoiceDateTime),
            SellerTin = sellerTin,
            BuyerTin = NullIfWhiteSpace(request.InvoiceHeader.BuyerTin),
            BuyerName = NullIfWhiteSpace(request.InvoiceHeader.BuyerName),
            BuyerAuthorizationCode = NullIfWhiteSpace(request.InvoiceHeader.BuyerAuthorizationCode),
            SiteId = siteId,
            GlobalConfigVersion = EnsureConfigVersion(
                identity?.GlobalConfigVersion ?? request.InvoiceHeader.GlobalConfigVersion),
            TaxpayerConfigVersion = EnsureConfigVersion(
                identity?.TaxpayerConfigVersion ?? request.InvoiceHeader.TaxpayerConfigVersion),
            TerminalConfigVersion = EnsureConfigVersion(
                identity?.TerminalConfigVersion ?? request.InvoiceHeader.TerminalConfigVersion),
            IsReliefSupply = request.InvoiceHeader.IsReliefSupply,
            Vat5CertificateDetails = request.InvoiceHeader.Vat5CertificateDetails,
            PaymentMethod = string.IsNullOrWhiteSpace(request.InvoiceHeader.PaymentMethod)
                ? "Cash"
                : request.InvoiceHeader.PaymentMethod.Trim()
        };

        var lines = new List<InvoiceLineItemDto>(request.InvoiceLineItems.Count);
        for (var i = 0; i < request.InvoiceLineItems.Count; i++)
        {
            var line = request.InvoiceLineItems[i];
            var taxRateId = MraTaxRateCodes.Normalize(line.TaxRateId);
            if (MraTaxRateCodes.IsStandardVatTier(taxRateId)
                && !string.IsNullOrWhiteSpace(standardTaxRateId))
            {
                taxRateId = standardTaxRateId;
            }

            var unitPrice = PosTaxCalculator.RoundMoney(line.UnitPrice);
            var quantity = RoundQuantity(line.Quantity);
            var discount = PosTaxCalculator.RoundMoney(line.Discount);
            var net = PosTaxCalculator.RoundMoney(line.Total);
            var vat = PosTaxCalculator.RoundMoney(line.TotalVat);

            // Re-align VAT to the configured rate when the line is on the standard tier
            // and the stored VAT does not match (prevents sandbox 500 from rate drift).
            var ratePercent = MraTaxRateCodes.ResolveRatePercent(taxRateId, configuredRates);
            if (ratePercent > 0m)
            {
                var expectedVat = PosTaxCalculator.CalculateVatAmount(net, ratePercent);
                if (Math.Abs(expectedVat - vat) > 0.02m)
                {
                    vat = expectedVat;
                }
            }

            lines.Add(new InvoiceLineItemDto
            {
                Id = i + 1,
                ProductCode = line.ProductCode.Trim(),
                Description = line.Description.Trim(),
                UnitPrice = unitPrice,
                Quantity = quantity,
                Discount = discount,
                Total = net,
                TotalVat = vat,
                TaxRateId = taxRateId,
                IsProduct = line.IsProduct
            });
        }

        var taxBreakDown = lines
            .GroupBy(l => l.TaxRateId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TaxBreakDownDto
            {
                RateId = g.Key,
                TaxableAmount = PosTaxCalculator.RoundMoney(g.Sum(x => x.Total)),
                TaxAmount = PosTaxCalculator.RoundMoney(g.Sum(x => x.TotalVat))
            })
            .ToList();

        var totalVat = PosTaxCalculator.RoundMoney(lines.Sum(x => x.TotalVat));
        var invoiceTotal = PosTaxCalculator.RoundMoney(lines.Sum(x => x.Total) + totalVat);
        var amountTendered = PosTaxCalculator.RoundMoney(
            request.InvoiceSummary.AmountTendered > 0
                ? request.InvoiceSummary.AmountTendered
                : invoiceTotal);

        var summary = request.InvoiceSummary with
        {
            TaxBreakDown = taxBreakDown,
            LevyBreakDown = request.InvoiceSummary.LevyBreakDown?
                .Select(l => new LevyBreakDownDto
                {
                    LevyTypeId = l.LevyTypeId.Trim(),
                    LevyRate = l.LevyRate,
                    LevyAmount = PosTaxCalculator.RoundMoney(l.LevyAmount)
                })
                .ToList(),
            OfflineSignature = NullIfWhiteSpace(request.InvoiceSummary.OfflineSignature),
            TotalVat = totalVat,
            InvoiceTotal = invoiceTotal,
            AmountTendered = amountTendered < invoiceTotal ? invoiceTotal : amountTendered
        };

        return request with
        {
            InvoiceHeader = header,
            InvoiceLineItems = lines,
            InvoiceSummary = summary
        };
    }

    /// <summary>
    /// Converts human-readable site labels (e.g. "City Center") into MRA-style codes
    /// (<c>SITE-CITY-CENTER</c>). Already-coded values are left unchanged.
    /// </summary>
    public static string NormalizeSiteId(string? siteId)
    {
        var raw = siteId?.Trim() ?? string.Empty;
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        // Already looks like an EIS site code (SITE001, SITE-01, GUID, etc.).
        if (!raw.Contains(' ', StringComparison.Ordinal) &&
            !raw.Contains('\t', StringComparison.Ordinal) &&
            (raw.StartsWith("SITE", StringComparison.OrdinalIgnoreCase)
             || Guid.TryParse(raw, out _)
             || !SiteDisplayNamePattern().IsMatch(raw)))
        {
            return raw;
        }

        var slug = SiteSlugInvalidChars().Replace(raw.ToUpperInvariant(), "-");
        slug = CollapseHyphens().Replace(slug, "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "SITE-LOCAL";
        }

        return slug.StartsWith("SITE-", StringComparison.OrdinalIgnoreCase)
            ? slug
            : "SITE-" + slug;
    }

    public static int EnsureConfigVersion(int version) => version > 0 ? version : 1;

    private static string ResolveOverlayStandardTaxRateId(
        MraFiscalIdentityOverlay? identity,
        SubmitSalesTransactionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(identity?.StandardTaxRateId))
        {
            return identity.StandardTaxRateId.Trim();
        }

        if (identity?.ConfiguredTaxRates is { Count: > 0 } rates)
        {
            return MraTaxRateCodes.ResolveStandardRateId(rates, activatedTaxRateIds: null);
        }

        // Preserve payload's existing standard-tier id (A / T / STANDARD_17_5 → A).
        var fromLines = request.InvoiceLineItems
            .Select(l => l.TaxRateId)
            .FirstOrDefault(id => MraTaxRateCodes.IsStandardVatTier(id));
        if (!string.IsNullOrWhiteSpace(fromLines))
        {
            return MraTaxRateCodes.Normalize(fromLines);
        }

        var fromBreakdown = request.InvoiceSummary.TaxBreakDown?
            .Select(t => t.RateId)
            .FirstOrDefault(id => MraTaxRateCodes.IsStandardVatTier(id));
        if (!string.IsNullOrWhiteSpace(fromBreakdown))
        {
            return MraTaxRateCodes.Normalize(fromBreakdown);
        }

        return MraTaxRateCodes.StandardVat;
    }

    private static decimal RoundQuantity(decimal quantity) =>
        Math.Round(quantity, 3, MidpointRounding.AwayFromZero);

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"[^A-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SiteSlugInvalidChars();

    [GeneratedRegex(@"-{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex CollapseHyphens();

    [GeneratedRegex(@"[A-Za-z].*[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex SiteDisplayNamePattern();
}

/// <summary>Optional identity overlay applied when Force Sync / Retry refreshes from live config.</summary>
public sealed record MraFiscalIdentityOverlay(
    string? SellerTin = null,
    string? SiteId = null,
    int? GlobalConfigVersion = null,
    int? TaxpayerConfigVersion = null,
    int? TerminalConfigVersion = null,
    string? StandardTaxRateId = null,
    IReadOnlyList<(string Id, decimal Rate)>? ConfiguredTaxRates = null);
