using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.ViewModels;

public partial class DiscountManagementViewModel : ObservableObject
{
    private readonly IPricingRuleRepository _ruleRepository;
    private readonly IPricingRulesEngine _pricingEngine;
    private readonly IAuthenticationAuthorizationService _auth;

    public DiscountManagementViewModel(
        IPricingRuleRepository ruleRepository,
        IPricingRulesEngine pricingEngine,
        IAuthenticationAuthorizationService auth)
    {
        _ruleRepository = ruleRepository;
        _pricingEngine = pricingEngine;
        _auth = auth;
        Rules = new ObservableCollection<PricingRule>();
        ActivePromotions = new ObservableCollection<string>();
        RuleTypeOptions = new ObservableCollection<string>(PricingRuleTypes.All);
        NewRuleType = PricingRuleTypes.CategoryPercent;
        NewStartsAt = DateTime.Today;
        _ = RefreshAsync();
    }

    public ObservableCollection<PricingRule> Rules { get; }
    public ObservableCollection<string> ActivePromotions { get; }
    public ObservableCollection<string> RuleTypeOptions { get; }

    [ObservableProperty]
    private PricingRule? _selectedRule;

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newRuleType;

    [ObservableProperty]
    private string _newCategoryCode = string.Empty;

    [ObservableProperty]
    private string _newProductCode = string.Empty;

    [ObservableProperty]
    private decimal _newPercentOff;

    [ObservableProperty]
    private decimal _newBuyQuantity = 1;

    [ObservableProperty]
    private decimal _newFreeQuantity = 1;

    [ObservableProperty]
    private decimal _newPromoUnitPrice;

    [ObservableProperty]
    private DateTime _newStartsAt;

    [ObservableProperty]
    private DateTime? _newEndsAt;

    [ObservableProperty]
    private int _newPriority = 100;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManageLoyaltyPrograms);
            Rules.Clear();
            foreach (var rule in await _ruleRepository.GetAllAsync().ConfigureAwait(true))
            {
                Rules.Add(rule);
            }

            ActivePromotions.Clear();
            var active = await _ruleRepository.GetActiveAsync(DateTime.UtcNow).ConfigureAwait(true);
            foreach (var name in active.Select(r => r.Name))
            {
                ActivePromotions.Add(name);
            }

            StatusMessage = $"{Rules.Count} rule(s); {ActivePromotions.Count} currently active.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task CreateRuleAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManageLoyaltyPrograms);
            if (string.IsNullOrWhiteSpace(NewName))
            {
                StatusMessage = "Rule name is required.";
                return;
            }

            var rule = new PricingRule
            {
                Name = NewName.Trim(),
                RuleType = NewRuleType,
                CategoryCode = string.IsNullOrWhiteSpace(NewCategoryCode) ? null : NewCategoryCode.Trim(),
                ProductCode = string.IsNullOrWhiteSpace(NewProductCode) ? null : NewProductCode.Trim(),
                PercentOff = NewPercentOff,
                BuyQuantity = NewBuyQuantity,
                FreeQuantity = NewFreeQuantity,
                PromoUnitPrice = NewRuleType == PricingRuleTypes.PromoPrice ? NewPromoUnitPrice : null,
                StartsAtUtc = DateTime.SpecifyKind(NewStartsAt.Date, DateTimeKind.Local).ToUniversalTime(),
                EndsAtUtc = NewEndsAt is null
                    ? null
                    : DateTime.SpecifyKind(NewEndsAt.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime(),
                Priority = NewPriority,
                IsActive = true
            };

            rule.RuleId = await _ruleRepository.CreateAsync(rule).ConfigureAwait(true);
            NewName = string.Empty;
            StatusMessage = $"Created rule #{rule.RuleId}.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ToggleSelectedAsync()
    {
        if (SelectedRule is null)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManageLoyaltyPrograms);
            var next = !SelectedRule.IsActive;
            await _ruleRepository.SetActiveAsync(SelectedRule.RuleId, next).ConfigureAwait(true);
            StatusMessage = next ? "Rule activated." : "Rule deactivated.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task PreviewEngineAsync()
    {
        try
        {
            var sample = new[]
            {
                new PricingCartLine
                {
                    ProductCode = "SAMPLE-A",
                    Description = "Sample",
                    CategoryCode = "T",
                    UnitPrice = 1000m,
                    Quantity = 3,
                    VatRatePercent = 17.5m
                }
            };
            var result = await _pricingEngine.EvaluateAsync(sample).ConfigureAwait(true);
            StatusMessage =
                $"Preview discount net {result.TotalDiscountNet:N2} via: {string.Join(", ", result.AppliedPromotionNames.DefaultIfEmpty("none"))}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}
