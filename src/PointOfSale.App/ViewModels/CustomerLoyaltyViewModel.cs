using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class CustomerLoyaltyViewModel : ObservableObject
{
    private readonly ILoyaltyProgramService _loyaltyService;
    private readonly IAuthenticationAuthorizationService _auth;

    public CustomerLoyaltyViewModel(
        ILoyaltyProgramService loyaltyService,
        IAuthenticationAuthorizationService auth)
    {
        _loyaltyService = loyaltyService;
        _auth = auth;
        SearchResults = new ObservableCollection<LoyaltyMember>();
        Ledger = new ObservableCollection<LoyaltyLedgerEntry>();
    }

    public ObservableCollection<LoyaltyMember> SearchResults { get; }
    public ObservableCollection<LoyaltyLedgerEntry> Ledger { get; }

    [ObservableProperty]
    private string _customerSearchQuery = string.Empty;

    [ObservableProperty]
    private LoyaltyMember? _selectedMember;

    [ObservableProperty]
    private decimal _availablePoints;

    [ObservableProperty]
    private string _enrollName = string.Empty;

    [ObservableProperty]
    private string _enrollPhone = string.Empty;

    [ObservableProperty]
    private string _enrollCode = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Search by code, name, or phone.";

    partial void OnSelectedMemberChanged(LoyaltyMember? value)
    {
        AvailablePoints = value?.PointsBalance ?? 0;
        _ = LoadLedgerAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.LookupLoyaltyCustomer);
            SearchResults.Clear();
            foreach (var member in await _loyaltyService.SearchAsync(CustomerSearchQuery).ConfigureAwait(true))
            {
                SearchResults.Add(member);
            }

            StatusMessage = SearchResults.Count == 0
                ? "No members matched."
                : $"Found {SearchResults.Count} member(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task EnrollAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.LookupLoyaltyCustomer);
            var member = await _loyaltyService.EnrollAsync(
                    EnrollName,
                    EnrollPhone,
                    string.IsNullOrWhiteSpace(EnrollCode) ? null : EnrollCode)
                .ConfigureAwait(true);
            EnrollName = string.Empty;
            EnrollPhone = string.Empty;
            EnrollCode = string.Empty;
            CustomerSearchQuery = member.MemberCode;
            SelectedMember = member;
            AvailablePoints = member.PointsBalance;
            SearchResults.Clear();
            SearchResults.Add(member);
            StatusMessage = $"Enrolled {member.FullName} ({member.MemberCode}).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task LoadLedgerAsync()
    {
        Ledger.Clear();
        if (SelectedMember is null)
        {
            return;
        }

        try
        {
            foreach (var entry in await _loyaltyService.GetLedgerAsync(SelectedMember.MemberId).ConfigureAwait(true))
            {
                Ledger.Add(entry);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}
