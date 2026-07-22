using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Cashier POS workspace: sales (nested checkout), shift/drawer, Z-report, and VAT-aware totals.
/// </summary>
public partial class CashierDashboardViewModel : ObservableObject
{
    private readonly IShiftManagementService _shifts;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly IHardwarePeripheralService _hardware;
    private readonly INavigationService _navigation;
    private readonly ISupervisorAuthorizationService _supervisorAuthorization;
    private readonly ISupervisorOverrideDialogService _supervisorDialog;

    public CashierDashboardViewModel(
        CheckoutViewModel checkout,
        IShiftManagementService shifts,
        IAuthenticationAuthorizationService auth,
        IHardwarePeripheralService hardware,
        INavigationService navigation,
        ISupervisorAuthorizationService supervisorAuthorization,
        ISupervisorOverrideDialogService supervisorDialog)
    {
        Checkout = checkout;
        _shifts = shifts;
        _auth = auth;
        _hardware = hardware;
        _navigation = navigation;
        _supervisorAuthorization = supervisorAuthorization;
        _supervisorDialog = supervisorDialog;
        SelectedWorkspaceTab = 0;
        PaymentMethodOptions = new[] { "Cash", "Card", "MobileMoney", "Split" };
        _ = RefreshShiftAsync();
    }

    public CheckoutViewModel Checkout { get; }

    public string[] PaymentMethodOptions { get; }

    public decimal StatutoryVatRatePercent => PosTaxCalculator.MalawiStandardVatRatePercent;

    [ObservableProperty]
    private int _selectedWorkspaceTab;

    [ObservableProperty]
    private string _statusMessage = "Cashier workspace ready.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private CashierShift? _openShift;

    [ObservableProperty]
    private decimal _openingFloat = 500m;

    [ObservableProperty]
    private decimal _closingCashCounted;

    [ObservableProperty]
    private string _shiftNotes = string.Empty;

    [ObservableProperty]
    private string _zReportPreview = string.Empty;

    [ObservableProperty]
    private bool _isCashDrawerReady;

    [ObservableProperty]
    private decimal _refundAmount;

    [ObservableProperty]
    private string _refundReason = string.Empty;

    [ObservableProperty]
    private decimal _splitCashAmount;

    [ObservableProperty]
    private decimal _splitCardAmount;

    [RelayCommand]
    private async Task RefreshShiftAsync()
    {
        try
        {
            OpenShift = await _shifts.GetOpenShiftAsync().ConfigureAwait(true);
            var health = await _hardware.ProbeAsync().ConfigureAwait(true);
            IsCashDrawerReady = health.IsCashDrawerReady;
            StatusMessage = OpenShift is null
                ? "No open shift — open the drawer float before selling."
                : $"Shift {OpenShift.ShiftId} open for {OpenShift.CashierName}.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task OpenShiftAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var name = _auth.CurrentOperator?.DisplayName ?? Environment.UserName;
            OpenShift = await _shifts.OpenShiftAsync(name, OpeningFloat).ConfigureAwait(true);
            StatusMessage = $"Shift {OpenShift.ShiftId} opened with float {OpeningFloat:N2} MWK.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task CloseShiftAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var bundle = await _shifts.CloseShiftAsync(ClosingCashCounted, ShiftNotes).ConfigureAwait(true);
            OpenShift = null;
            ZReportPreview =
                $"Z-Report Shift {bundle.ShiftId}\n" +
                $"Cash counted: {bundle.ClosingCashCounted:N2}\n" +
                $"Expected: {bundle.ExpectedCashInDrawer:N2}\n" +
                $"Variance: {bundle.CashVariance:N2}\n" +
                $"Gross: {bundle.GrossSales:N2} · VAT: {bundle.TotalVat:N2}\n" +
                $"Cash {bundle.CashSales:N2} · Card {bundle.CardSales:N2} · Mobile {bundle.MobileMoneySales:N2}";
            StatusMessage = "Shift closed — Z-report generated.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task PreviewZReportAsync()
    {
        try
        {
            var preview = await _shifts.BuildZReportPreviewAsync().ConfigureAwait(true);
            if (preview is null)
            {
                ZReportPreview = "No open shift to preview.";
                return;
            }

            ZReportPreview =
                $"PREVIEW Shift {preview.ShiftId} · {preview.CashierName}\n" +
                $"Gross sales: {preview.GrossSales:N2} MWK\n" +
                $"VAT ({StatutoryVatRatePercent:N1}%): {preview.TotalVat:N2} MWK\n" +
                $"Expected cash: {preview.ExpectedCashInDrawer:N2}\n" +
                $"Cash {preview.CashSales:N2} · Card {preview.CardSales:N2} · Mobile {preview.MobileMoneySales:N2}";
            StatusMessage = "Z-report preview refreshed.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task KickDrawerAsync()
    {
        try
        {
            if (!_auth.HasPermission(OperatorPermissions.OpenCashDrawer))
            {
                var overrideResult = await _supervisorDialog.PromptAsync(
                        new SupervisorOverrideRequest
                        {
                            ActionType = SupervisorOverrideActions.CashDrawerOverride,
                            RequiredPermission = OperatorPermissions.OpenCashDrawer,
                            Reason = "Authorize cash drawer kick (ESC p) override."
                        })
                    .ConfigureAwait(true);

                if (!overrideResult.Authorized)
                {
                    StatusMessage = overrideResult.Error ?? "Cash drawer override denied.";
                    return;
                }
            }
            else
            {
                _auth.EnsurePermission(OperatorPermissions.OpenCashDrawer);
                await _supervisorAuthorization.AuthorizeAsync(
                        new SupervisorOverrideRequest
                        {
                            ActionType = SupervisorOverrideActions.CashDrawerOverride,
                            RequiredPermission = OperatorPermissions.OpenCashDrawer,
                            Reason = "Session cash drawer kick.",
                            AllowCurrentSession = true
                        })
                    .ConfigureAwait(true);
            }

            await _hardware.KickCashDrawerAsync().ConfigureAwait(true);
            IsCashDrawerReady = true;
            StatusMessage = "Cash drawer kick sent.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void ApplySplitTender()
    {
        var total = SplitCashAmount + SplitCardAmount;
        Checkout.PaymentMethod = "Split";
        Checkout.AmountTendered = total;
        StatusMessage = $"Split tender set: cash {SplitCashAmount:N2} + card {SplitCardAmount:N2} = {total:N2}.";
    }

    [RelayCommand]
    private async Task RequestRefundOverrideAsync()
    {
        if (RefundAmount <= 0)
        {
            StatusMessage = "Enter a refund amount greater than zero.";
            return;
        }

        if (string.IsNullOrWhiteSpace(RefundReason))
        {
            StatusMessage = "Enter a refund / exchange reason.";
            return;
        }

        SupervisorAuthorizationResult result;
        if (_auth.HasPermission(OperatorPermissions.PerformVoid))
        {
            result = await _supervisorAuthorization.AuthorizeAsync(
                    new SupervisorOverrideRequest
                    {
                        ActionType = SupervisorOverrideActions.PostShiftReturn,
                        RequiredPermission = OperatorPermissions.PerformVoid,
                        Reason = RefundReason,
                        Detail = $"amount={RefundAmount:N2}",
                        AllowCurrentSession = true
                    })
                .ConfigureAwait(true);
        }
        else
        {
            result = await _supervisorDialog.PromptAsync(
                    new SupervisorOverrideRequest
                    {
                        ActionType = SupervisorOverrideActions.PostShiftReturn,
                        RequiredPermission = OperatorPermissions.PerformVoid,
                        Reason = $"Authorize post-shift return / exchange of {RefundAmount:N2} MWK — {RefundReason}.",
                        Detail = $"amount={RefundAmount:N2}; reason={RefundReason}"
                    })
                .ConfigureAwait(true);
        }

        if (!result.Authorized)
        {
            StatusMessage = result.Error ?? "Refund blocked — supervisor authorization rejected.";
            return;
        }

        StatusMessage =
            $"Refund/exchange authorized by {result.AuthorizingUsername} for {RefundAmount:N2} MWK ({RefundReason}). " +
            "Complete via void/reprint on the sales cart with fiscal credit note if required.";
    }

    [RelayCommand]
    private void OpenStandaloneCheckout() => _navigation.NavigateTo<CheckoutViewModel>();

    [RelayCommand]
    private void OpenQueue() => _navigation.NavigateTo<QueueSyncStatusViewModel>();
}
