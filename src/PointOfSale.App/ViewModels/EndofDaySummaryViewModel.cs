using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class EndofDaySummaryViewModel : ObservableObject
{
    private readonly IFinancialClosureService _closureService;
    private readonly IZReportPrintingService _zReportPrinting;
    private readonly IAuthenticationAuthorizationService _auth;

    public EndofDaySummaryViewModel(
        IFinancialClosureService closureService,
        IZReportPrintingService zReportPrinting,
        IAuthenticationAuthorizationService auth)
    {
        _closureService = closureService;
        _zReportPrinting = zReportPrinting;
        _auth = auth;
        DayShifts = new ObservableCollection<CashierShift>();
        RecentClosures = new ObservableCollection<FinancialClosureRecord>();
        FiscalInvoices = new ObservableCollection<ZReportInvoiceLine>();
        BusinessDate = DateTime.Today;
        _ = RefreshAsync();
    }

    public ObservableCollection<CashierShift> DayShifts { get; }
    public ObservableCollection<FinancialClosureRecord> RecentClosures { get; }
    public ObservableCollection<ZReportInvoiceLine> FiscalInvoices { get; }

    [ObservableProperty]
    private DateTime _businessDate = DateTime.Today;

    [ObservableProperty]
    private decimal _totalGrossSalesMwk;

    [ObservableProperty]
    private decimal _totalVatCollectedMwk;

    [ObservableProperty]
    private decimal _totalTaxableSalesMwk;

    [ObservableProperty]
    private decimal _cashCollectionsMwk;

    [ObservableProperty]
    private decimal _cardSettlementsMwk;

    [ObservableProperty]
    private decimal _mobileMoneySettlementsMwk;

    [ObservableProperty]
    private decimal _cashDrawerVarianceMwk;

    [ObservableProperty]
    private decimal _expectedVatMwk;

    [ObservableProperty]
    private decimal _vatVarianceMwk;

    [ObservableProperty]
    private decimal _totalVoidsMwk;

    [ObservableProperty]
    private decimal _cumulativeGrossSalesMwk;

    [ObservableProperty]
    private decimal _cumulativeVatMwk;

    [ObservableProperty]
    private bool _isShiftClosed;

    [ObservableProperty]
    private bool _isDayClosed;

    [ObservableProperty]
    private bool _hasOpenShift;

    [ObservableProperty]
    private bool _auditPassed;

    [ObservableProperty]
    private bool _isVatBalanced;

    [ObservableProperty]
    private string _zReportDetails = string.Empty;

    [ObservableProperty]
    private string _auditMessage = string.Empty;

    [ObservableProperty]
    private string _closureNotes = string.Empty;

    [ObservableProperty]
    private bool _managerOverride;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "End-of-Day fiscal closure and Z-Report.";

    [ObservableProperty]
    private EndOfDaySummary? _currentSummary;

    [ObservableProperty]
    private ZReportBundle? _currentZReport;

    partial void OnBusinessDateChanged(DateTime value) => _ = RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.CloseFinancialDay);
            IsBusy = true;
            var summary = await _closureService.BuildPreviewAsync(BusinessDate).ConfigureAwait(true);
            ApplySummary(summary);

            RecentClosures.Clear();
            foreach (var row in await _closureService.GetRecentClosuresAsync().ConfigureAwait(true))
            {
                RecentClosures.Add(row);
            }

            StatusMessage = summary.SummaryText;
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
    private async Task CloseFinancialDayAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.CloseFinancialDay);

            var confirm = MessageBox.Show(
                Application.Current.MainWindow,
                $"Close business day {BusinessDate:yyyy-MM-dd}?\n\n"
                + $"Gross sales: {TotalGrossSalesMwk:N2} MWK\n"
                + $"VAT collected: {TotalVatCollectedMwk:N2} MWK\n"
                + $"Cash variance: {CashDrawerVarianceMwk:N2} MWK\n\n"
                + "Manager authorization is required. This action cannot be undone.",
                "Confirm End-of-Day Closure",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                StatusMessage = "EOD closure cancelled.";
                return;
            }

            IsBusy = true;
            var result = await _closureService.CloseBusinessDayAsync(
                    BusinessDate,
                    ClosureNotes,
                    ManagerOverride)
                .ConfigureAwait(true);

            ApplySummary(result.Summary);
            CurrentZReport = result.ZReport;
            StatusMessage = result.Message;

            RecentClosures.Clear();
            foreach (var row in await _closureService.GetRecentClosuresAsync().ConfigureAwait(true))
            {
                RecentClosures.Add(row);
            }
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
    private async Task PrintZReportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.CloseFinancialDay);
            var report = CurrentZReport
                ?? CurrentSummary?.AggregatedZReport
                ?? throw new InvalidOperationException("Refresh the EOD summary before printing a Z-Report.");

            IsBusy = true;
            var session = _auth.CurrentOperator;
            var result = await _zReportPrinting.PrintAsync(
                    report,
                    new ZReportPrintContext
                    {
                        ManagerSignOff = session is null
                            ? string.Empty
                            : $"{session.DisplayName} ({session.Username})",
                        BusinessDate = BusinessDate,
                        CumulativeGrossSalesMwk = CumulativeGrossSalesMwk,
                        CumulativeVatMwk = CumulativeVatMwk,
                        TotalVoidsMwk = TotalVoidsMwk,
                        VoidCount = CurrentSummary?.VoidCount ?? 0,
                        AuditPassed = AuditPassed,
                        AuditMessage = AuditMessage
                    })
                .ConfigureAwait(true);

            StatusMessage = result.Message;
            ZReportDetails = _zReportPrinting.FormatPlainText(
                report,
                new ZReportPrintContext
                {
                    ManagerSignOff = session?.DisplayName ?? string.Empty,
                    BusinessDate = BusinessDate,
                    CumulativeGrossSalesMwk = CumulativeGrossSalesMwk,
                    CumulativeVatMwk = CumulativeVatMwk,
                    TotalVoidsMwk = TotalVoidsMwk,
                    VoidCount = CurrentSummary?.VoidCount ?? 0,
                    AuditPassed = AuditPassed,
                    AuditMessage = AuditMessage
                });
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

    private void ApplySummary(EndOfDaySummary summary)
    {
        CurrentSummary = summary;
        CurrentZReport = summary.AggregatedZReport;
        TotalGrossSalesMwk = summary.TotalGrossSalesMwk;
        TotalVatCollectedMwk = summary.TotalVatCollectedMwk;
        TotalTaxableSalesMwk = summary.TotalTaxableSalesMwk;
        CashCollectionsMwk = summary.CashCollectionsMwk;
        CardSettlementsMwk = summary.CardSettlementsMwk;
        MobileMoneySettlementsMwk = summary.MobileMoneySettlementsMwk;
        CashDrawerVarianceMwk = summary.CashDrawerVarianceMwk;
        ExpectedVatMwk = summary.ExpectedVatMwk;
        VatVarianceMwk = summary.VatVarianceMwk;
        TotalVoidsMwk = summary.TotalVoidsMwk;
        CumulativeGrossSalesMwk = summary.CumulativeGrossSalesMwk;
        CumulativeVatMwk = summary.CumulativeVatMwk;
        IsDayClosed = summary.IsDayAlreadyClosed;
        HasOpenShift = summary.HasOpenShift;
        IsShiftClosed = !summary.HasOpenShift && summary.ShiftCount > 0
            && summary.Shifts.All(s => s.Status == ShiftStatuses.Closed);
        AuditPassed = summary.AuditPassed;
        IsVatBalanced = summary.IsVatBalanced;
        AuditMessage = summary.AuditMessage;
        ZReportDetails = summary.AggregatedZReport is null
            ? summary.SummaryText
            : _zReportPrinting.FormatPlainText(
                summary.AggregatedZReport,
                new ZReportPrintContext
                {
                    BusinessDate = summary.BusinessDate,
                    CumulativeGrossSalesMwk = summary.CumulativeGrossSalesMwk,
                    CumulativeVatMwk = summary.CumulativeVatMwk,
                    TotalVoidsMwk = summary.TotalVoidsMwk,
                    VoidCount = summary.VoidCount,
                    AuditPassed = summary.AuditPassed,
                    AuditMessage = summary.AuditMessage,
                    ManagerSignOff = _auth.CurrentOperator?.DisplayName ?? string.Empty
                });

        DayShifts.Clear();
        foreach (var shift in summary.Shifts)
        {
            DayShifts.Add(shift);
        }

        FiscalInvoices.Clear();
        foreach (var invoice in summary.FiscalizedInvoices.Take(100))
        {
            FiscalInvoices.Add(invoice);
        }
    }
}
