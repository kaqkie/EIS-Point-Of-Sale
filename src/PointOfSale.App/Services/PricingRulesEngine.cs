using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.Services;

public interface IPricingRulesEngine
{
    Task<PricingEvaluationResult> EvaluateAsync(
        IReadOnlyList<PricingCartLine> cartLines,
        DateTime? asOfUtc = null,
        CancellationToken cancellationToken = default);

    PricingEvaluationResult Evaluate(IReadOnlyList<PricingCartLine> cartLines, IReadOnlyList<PricingRule> activeRules);
}

public sealed class LinePricingAdjustment
{
    public required string ProductCode { get; init; }
    public decimal DiscountNet { get; init; }
    public string AppliedRuleName { get; init; } = string.Empty;
    public string RuleType { get; init; } = string.Empty;
}

public sealed class PricingEvaluationResult
{
    public IReadOnlyList<LinePricingAdjustment> LineAdjustments { get; init; } = Array.Empty<LinePricingAdjustment>();
    public decimal TotalDiscountNet { get; init; }
    public IReadOnlyList<string> AppliedPromotionNames { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Promotional pricing engine: category %, BOGO, and time-bound promo unit prices.
/// Higher Priority wins; VAT remains PosTaxCalculator-aligned (discount reduces taxable net first).
/// </summary>
public sealed class PricingRulesEngine : IPricingRulesEngine
{
    private readonly IPricingRuleRepository _ruleRepository;

    public PricingRulesEngine(IPricingRuleRepository ruleRepository)
    {
        _ruleRepository = ruleRepository;
    }

    public async Task<PricingEvaluationResult> EvaluateAsync(
        IReadOnlyList<PricingCartLine> cartLines,
        DateTime? asOfUtc = null,
        CancellationToken cancellationToken = default)
    {
        var rules = await _ruleRepository.GetActiveAsync(asOfUtc ?? DateTime.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        return Evaluate(cartLines, rules);
    }

    public PricingEvaluationResult Evaluate(
        IReadOnlyList<PricingCartLine> cartLines,
        IReadOnlyList<PricingRule> activeRules)
    {
        var discounts = new Dictionary<string, (decimal Discount, string RuleName, string RuleType)>(
            StringComparer.OrdinalIgnoreCase);
        var appliedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var orderedRules = activeRules
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.RuleId)
            .ToList();

        foreach (var rule in orderedRules)
        {
            switch (rule.RuleType)
            {
                case PricingRuleTypes.PromoPrice:
                    ApplyPromoPrice(rule, cartLines, discounts, appliedNames);
                    break;
                case PricingRuleTypes.CategoryPercent:
                    ApplyCategoryPercent(rule, cartLines, discounts, appliedNames);
                    break;
                case PricingRuleTypes.Bogo:
                    ApplyBogo(rule, cartLines, discounts, appliedNames);
                    break;
            }
        }

        var adjustments = discounts
            .Select(kv => new LinePricingAdjustment
            {
                ProductCode = kv.Key,
                DiscountNet = PosTaxCalculator.RoundMoney(kv.Value.Discount),
                AppliedRuleName = kv.Value.RuleName,
                RuleType = kv.Value.RuleType
            })
            .Where(a => a.DiscountNet > 0)
            .ToList();

        return new PricingEvaluationResult
        {
            LineAdjustments = adjustments,
            TotalDiscountNet = PosTaxCalculator.RoundMoney(adjustments.Sum(a => a.DiscountNet)),
            AppliedPromotionNames = appliedNames.ToList()
        };
    }

    private static void ApplyPromoPrice(
        PricingRule rule,
        IReadOnlyList<PricingCartLine> cartLines,
        Dictionary<string, (decimal Discount, string RuleName, string RuleType)> discounts,
        HashSet<string> appliedNames)
    {
        if (string.IsNullOrWhiteSpace(rule.ProductCode) || rule.PromoUnitPrice is null)
        {
            return;
        }

        foreach (var line in cartLines.Where(l =>
                     l.ProductCode.Equals(rule.ProductCode, StringComparison.OrdinalIgnoreCase)))
        {
            if (discounts.ContainsKey(line.ProductCode))
            {
                continue; // higher-priority rule already claimed this line
            }

            if (rule.PromoUnitPrice.Value >= line.UnitPrice)
            {
                continue;
            }

            var fullNet = PosTaxCalculator.CalculateNetAmount(line.UnitPrice, line.Quantity);
            var promoNet = PosTaxCalculator.CalculateNetAmount(rule.PromoUnitPrice.Value, line.Quantity);
            var discount = PosTaxCalculator.RoundMoney(fullNet - promoNet);
            if (discount <= 0)
            {
                continue;
            }

            discounts[line.ProductCode] = (discount, rule.Name, rule.RuleType);
            appliedNames.Add(rule.Name);
        }
    }

    private static void ApplyCategoryPercent(
        PricingRule rule,
        IReadOnlyList<PricingCartLine> cartLines,
        Dictionary<string, (decimal Discount, string RuleName, string RuleType)> discounts,
        HashSet<string> appliedNames)
    {
        if (rule.PercentOff <= 0 || string.IsNullOrWhiteSpace(rule.CategoryCode))
        {
            return;
        }

        foreach (var line in cartLines)
        {
            if (discounts.ContainsKey(line.ProductCode))
            {
                continue;
            }

            var category = line.CategoryCode ?? string.Empty;
            if (!category.Equals(rule.CategoryCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullNet = PosTaxCalculator.CalculateNetAmount(line.UnitPrice, line.Quantity);
            var discount = PosTaxCalculator.RoundMoney(fullNet * rule.PercentOff / 100m);
            if (discount <= 0)
            {
                continue;
            }

            discounts[line.ProductCode] = (discount, rule.Name, rule.RuleType);
            appliedNames.Add(rule.Name);
        }
    }

    private static void ApplyBogo(
        PricingRule rule,
        IReadOnlyList<PricingCartLine> cartLines,
        Dictionary<string, (decimal Discount, string RuleName, string RuleType)> discounts,
        HashSet<string> appliedNames)
    {
        var buy = rule.BuyQuantity <= 0 ? 1m : rule.BuyQuantity;
        var free = rule.FreeQuantity <= 0 ? 1m : rule.FreeQuantity;
        var groupSize = buy + free;

        foreach (var line in cartLines)
        {
            if (discounts.ContainsKey(line.ProductCode))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rule.ProductCode) &&
                !line.ProductCode.Equals(rule.ProductCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (line.Quantity < groupSize)
            {
                continue;
            }

            var freeUnits = Math.Floor(line.Quantity / groupSize) * free;
            if (freeUnits <= 0)
            {
                continue;
            }

            var discount = PosTaxCalculator.CalculateNetAmount(line.UnitPrice, freeUnits);
            discounts[line.ProductCode] = (discount, rule.Name, rule.RuleType);
            appliedNames.Add(rule.Name);
        }
    }
}
