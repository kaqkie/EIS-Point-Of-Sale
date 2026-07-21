using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;

namespace PointOfSale.App.ViewModels;

public partial class AdminAnalyticsViewModel : ObservableObject
{
    private readonly ITaxReconciliationService _taxReconciliationService;
    private readonly IShiftManagementService _shiftManagementService;
    private readonly IAnalyticsReportExportService _exportService;

    public AdminAnalyticsViewModel(
        ITaxReconciliationService taxReconciliationService,
        IShiftManagementService shiftManagementService,
        IAnalyticsReportExportService exportService)
    {
        _taxReconciliationService = taxReconciliationService;
        _shiftManagementService = shiftManagementService;
        _exportService = exportService;

        TaxBuckets = new ObservableCollection<TaxCodeBucket>();
        HourlySales = new ObservableCollection<ChartBarItem>();
        QueueDrainage = new ObservableCollection<ChartBarItem>();
        RecentShifts = new ObservableCollection<CashierShift>();
        FiscalInvoices = new ObservableCollection<ZReportInvoiceLine>();

        SelectedPeriod = TaxReconciliationPeriod.Daily;
        CashierName = Environment.UserName;
        _ = RefreshAsync();
    }

    public ObservableCollection<TaxCodeBucket> TaxBuckets { get; }
    public ObservableCollection<ChartBarItem> HourlySales { get; }
    public ObservableCollection<ChartBarItem> QueueDrainage { get; }
    public ObservableCollection<CashierShift> RecentShifts { get; }
    public ObservableCollection<ZReportInvoiceLine> FiscalInvoices { get; }

    public Array PeriodOptions => Enum.GetValues(typeof(TaxReconciliationPeriod));

    [ObservableProperty]
    private TaxReconciliationPeriod _selectedPeriod;

    [ObservableProperty]
    private string _statusMessage = "Loading analytics...";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private decimal _grossSales;

    [ObservableProperty]
    private decimal _actualVat;

    [ObservableProperty]
    private decimal _expectedVat;

    [ObservableProperty]
    private decimal _vatVariance;

    [ObservableProperty]
    private bool _isTaxBalanced;

    [ObservableProperty]
    private int _syncedInvoiceCount;

    [ObservableProperty]
    private int _pendingQueueCount;

    [ObservableProperty]
    private int _quarantinedQueueCount;

    [ObservableProperty]
    private string _cashierName = string.Empty;

    [ObservableProperty]
    private decimal _openingFloat;

    [ObservableProperty]
    private decimal _cashMovementAmount;

    [ObservableProperty]
    private string _cashMovementReason = string.Empty;

    [ObservableProperty]
    private decimal _closingCashCounted;

    [ObservableProperty]
    private string? _openShiftSummary;

    [ObservableProperty]
    private ZReportBundle? _zReportPreview;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var report = await _taxReconciliationService.GetReportAsync(SelectedPeriod).ConfigureAwait(true);
            GrossSales = report.GrossSales;
            ActualVat = report.ActualVatCollected;
            ExpectedVat = report.ExpectedStandardVat;
            VatVariance = report.VatVariance;
            IsTaxBalanced = report.IsBalanced;
            SyncedInvoiceCount = report.SyncedInvoiceCount;

            TaxBuckets.Clear();
            foreach (var bucket in report.TaxBuckets)
            {
                TaxBuckets.Add(bucket);
            }

            var hourly = await _taxReconciliationService.GetHourlySalesVelocityAsync(DateTime.Today).ConfigureAwait(true);
            var maxSales = hourly.Count == 0 ? 1m : Math.Max(1m, hourly.Max(h => h.SalesTotal));
            HourlySales.Clear();
            foreach (var point in hourly)
            {
                HourlySales.Add(new ChartBarItem(
                    $"{point.HourUtc:00}:00",
                    point.SalesTotal,
                    (double)(point.SalesTotal / maxSales),
                    $"{point.InvoiceCount} inv"));
            }

            var health = await _taxReconciliationService.GetQueueHealthAsync().ConfigureAwait(true);
            PendingQueueCount = health.PendingCount + health.SyncingCount;
            QuarantinedQueueCount = health.QuarantinedCount;
            var drainage = health.HourlyDrainage;
            var maxQ = drainage.Count == 0
                ? 1
                : Math.Max(1, drainage.Max(h => Math.Max(h.SyncedCount, Math.Max(h.BacklogCount, h.QuarantinedCount))));
            QueueDrainage.Clear();
            foreach (var point in drainage.TakeLast(12))
            {
                QueueDrainage.Add(new ChartBarItem(
                    point.HourBucketUtc.ToLocalTime().ToString("HH:mm"),
                    point.SyncedCount,
                    point.SyncedCount / (double)maxQ,
                    $"Q:{point.QuarantinedCount} B:{point.BacklogCount}"));
            }

            var open = await _shiftManagementService.GetOpenShiftAsync().ConfigureAwait(true);
            OpenShiftSummary = open is null
                ? "No open shift"
                : $"Shift #{open.ShiftId} — {open.CashierName} (float {open.OpeningFloat:N2}) opened {open.OpenedAtUtc.ToLocalTime():g}";

            ZReportPreview = await _shiftManagementService.BuildZReportPreviewAsync().ConfigureAwait(true);
            FiscalInvoices.Clear();
            if (ZReportPreview is not null)
            {
                foreach (var inv in ZReportPreview.FiscalizedInvoices.Take(50))
                {
                    FiscalInvoices.Add(inv);
                }
            }

            RecentShifts.Clear();
            foreach (var shift in await _shiftManagementService.GetRecentShiftsAsync().ConfigureAwait(true))
            {
                RecentShifts.Add(shift);
            }

            StatusMessage = IsTaxBalanced
                ? "Tax reconciliation balanced (VAT variance < 0.01)."
                : $"Tax variance detected: {VatVariance:N2}. Investigate before audit handoff.";
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

    partial void OnSelectedPeriodChanged(TaxReconciliationPeriod value) => _ = RefreshAsync();

    [RelayCommand]
    private async Task OpenShiftAsync()
    {
        try
        {
            await _shiftManagementService.OpenShiftAsync(CashierName, OpeningFloat).ConfigureAwait(true);
            StatusMessage = "Shift opened.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task CashInAsync()
    {
        try
        {
            await _shiftManagementService.RecordCashInAsync(CashMovementAmount, CashMovementReason).ConfigureAwait(true);
            CashMovementAmount = 0;
            StatusMessage = "Cash-in recorded.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task CashOutAsync()
    {
        try
        {
            await _shiftManagementService.RecordCashOutAsync(CashMovementAmount, CashMovementReason).ConfigureAwait(true);
            CashMovementAmount = 0;
            StatusMessage = "Cash-out recorded.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task CloseShiftAsync()
    {
        try
        {
            var report = await _shiftManagementService.CloseShiftAsync(ClosingCashCounted).ConfigureAwait(true);
            ZReportPreview = report;
            StatusMessage = $"Shift closed. Cash variance {report.CashVariance:N2}.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExportTaxCsvAsync()
    {
        try
        {
            var report = await _taxReconciliationService.GetReportAsync(SelectedPeriod).ConfigureAwait(true);
            var path = await _exportService.ExportTaxReconciliationCsvAsync(report).ConfigureAwait(true);
            StatusMessage = string.IsNullOrEmpty(path) ? "CSV export cancelled." : $"CSV saved: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExportTaxPdfAsync()
    {
        try
        {
            var report = await _taxReconciliationService.GetReportAsync(SelectedPeriod).ConfigureAwait(true);
            await _exportService.ExportTaxReconciliationPdfAsync(report).ConfigureAwait(true);
            StatusMessage = "Tax PDF/print dialog completed.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExportZReportCsvAsync()
    {
        try
        {
            var report = ZReportPreview ?? await _shiftManagementService.BuildZReportPreviewAsync().ConfigureAwait(true);
            if (report is null)
            {
                StatusMessage = "No open shift Z-report available.";
                return;
            }

            var path = await _exportService.ExportZReportCsvAsync(report).ConfigureAwait(true);
            StatusMessage = string.IsNullOrEmpty(path) ? "CSV export cancelled." : $"Z-report CSV saved: {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExportZReportPdfAsync()
    {
        try
        {
            var report = ZReportPreview ?? await _shiftManagementService.BuildZReportPreviewAsync().ConfigureAwait(true);
            if (report is null)
            {
                StatusMessage = "No open shift Z-report available.";
                return;
            }

            await _exportService.ExportZReportPdfAsync(report).ConfigureAwait(true);
            StatusMessage = "Z-report PDF/print dialog completed.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}

public sealed record ChartBarItem(string Label, decimal Value, double Ratio, string Detail);
