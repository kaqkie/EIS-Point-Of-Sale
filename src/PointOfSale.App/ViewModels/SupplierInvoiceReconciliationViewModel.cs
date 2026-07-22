using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class SupplierInvoiceReconciliationViewModel : ObservableObject
{
    private readonly ISupplierInvoiceReconciliationService _reconciliationService;
    private readonly IAuthenticationAuthorizationService _auth;

    public SupplierInvoiceReconciliationViewModel(
        ISupplierInvoiceReconciliationService reconciliationService,
        IAuthenticationAuthorizationService auth)
    {
        _reconciliationService = reconciliationService;
        _auth = auth;
        PostedGrns = new ObservableCollection<GoodsReceiptNote>();
        Reconciliations = new ObservableCollection<SupplierInvoiceReconciliation>();
        DiscrepancyLines = new ObservableCollection<SupplierInvoiceReconciliationLine>();
        _ = RefreshAsync();
    }

    public ObservableCollection<GoodsReceiptNote> PostedGrns { get; }
    public ObservableCollection<SupplierInvoiceReconciliation> Reconciliations { get; }
    public ObservableCollection<SupplierInvoiceReconciliationLine> DiscrepancyLines { get; }

    [ObservableProperty]
    private GoodsReceiptNote? _selectedGrn;

    [ObservableProperty]
    private SupplierInvoiceReconciliation? _selectedReconciliation;

    [ObservableProperty]
    private string _supplierInvoiceNumber = string.Empty;

    [ObservableProperty]
    private DateTime? _invoiceDate = DateTime.Today;

    [ObservableProperty]
    private decimal _invoiceTotalMwk;

    [ObservableProperty]
    private string _discrepancyNotes = string.Empty;

    [ObservableProperty]
    private bool _isReconciling;

    [ObservableProperty]
    private string _statusMessage = "Reconcile posted GRNs against supplier tax invoices.";

    partial void OnSelectedGrnChanged(GoodsReceiptNote? value)
    {
        if (value is null)
        {
            return;
        }

        SupplierInvoiceNumber = value.SupplierInvoiceNumber ?? string.Empty;
    }

    partial void OnSelectedReconciliationChanged(SupplierInvoiceReconciliation? value) =>
        _ = LoadDiscrepanciesAsync(value);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ReconcileSupplierInvoices);
            PostedGrns.Clear();
            foreach (var grn in await _reconciliationService.GetPostedGrnsAsync().ConfigureAwait(true))
            {
                PostedGrns.Add(grn);
            }

            Reconciliations.Clear();
            foreach (var row in await _reconciliationService.GetRecentAsync().ConfigureAwait(true))
            {
                Reconciliations.Add(row);
            }

            StatusMessage = $"Loaded {PostedGrns.Count} posted GRN(s) and {Reconciliations.Count} reconciliation(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ReconcileAsync()
    {
        if (IsReconciling)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ReconcileSupplierInvoices);
            if (SelectedGrn is null)
            {
                StatusMessage = "Select a posted GRN.";
                return;
            }

            IsReconciling = true;
            var result = await _reconciliationService.ReconcileAsync(
                    SelectedGrn.GrnId,
                    SupplierInvoiceNumber,
                    InvoiceDate,
                    InvoiceTotalMwk,
                    invoiceQuantitiesByProduct: null,
                    invoiceUnitCostsByProduct: null,
                    _auth.CurrentOperator?.Username,
                    DiscrepancyNotes)
                .ConfigureAwait(true);

            StatusMessage = result.Message;
            DiscrepancyNotes = result.DiscrepancyNotes;
            DiscrepancyLines.Clear();
            foreach (var line in result.DiscrepancyLines)
            {
                DiscrepancyLines.Add(line);
            }

            await RefreshAsync().ConfigureAwait(true);
            SelectedReconciliation = Reconciliations.FirstOrDefault(r => r.ReconciliationId == result.ReconciliationId);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsReconciling = false;
        }
    }

    [RelayCommand]
    private async Task SignOffAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ReconcileSupplierInvoices);
            if (SelectedReconciliation is null)
            {
                StatusMessage = "Select a reconciliation to sign off.";
                return;
            }

            await _reconciliationService.SignOffAsync(SelectedReconciliation.ReconciliationId, DiscrepancyNotes)
                .ConfigureAwait(true);
            StatusMessage = $"Signed off reconciliation #{SelectedReconciliation.ReconciliationId}.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task LoadDiscrepanciesAsync(SupplierInvoiceReconciliation? row)
    {
        DiscrepancyLines.Clear();
        if (row is null)
        {
            return;
        }

        try
        {
            DiscrepancyNotes = row.DiscrepancyNotes ?? string.Empty;
            foreach (var line in await _reconciliationService.GetLinesAsync(row.ReconciliationId).ConfigureAwait(true))
            {
                DiscrepancyLines.Add(line);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}
