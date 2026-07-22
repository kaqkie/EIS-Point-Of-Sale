using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class GoodsReceiptViewModel : ObservableObject
{
    private readonly IGoodsReceiptService _grnService;
    private readonly INavigationService _navigationService;
    private readonly IAuthenticationAuthorizationService _auth;
    private GoodsReceiptDraft? _draft;

    public GoodsReceiptViewModel(
        IGoodsReceiptService grnService,
        INavigationService navigationService,
        IAuthenticationAuthorizationService auth)
    {
        _grnService = grnService;
        _navigationService = navigationService;
        _auth = auth;
        PurchaseOrders = new ObservableCollection<PurchaseOrder>();
        ReceivedItemsList = new ObservableCollection<GoodsReceiptLine>();
        RecentGrns = new ObservableCollection<GoodsReceiptNote>();
        _ = RefreshAsync();
    }

    public ObservableCollection<PurchaseOrder> PurchaseOrders { get; }
    public ObservableCollection<GoodsReceiptLine> ReceivedItemsList { get; }
    public ObservableCollection<GoodsReceiptNote> RecentGrns { get; }

    [ObservableProperty]
    private PurchaseOrder? _selectedPurchaseOrder;

    [ObservableProperty]
    private GoodsReceiptLine? _selectedReceivedItem;

    [ObservableProperty]
    private string _barcodeScanInput = string.Empty;

    [ObservableProperty]
    private string _deliveryNoteNumber = string.Empty;

    [ObservableProperty]
    private string _supplierInvoiceNumber = string.Empty;

    [ObservableProperty]
    private string _notes = string.Empty;

    [ObservableProperty]
    private bool _isProcessingGrn;

    [ObservableProperty]
    private string _statusMessage = "Select a purchase order and scan or enter received quantities.";

    [ObservableProperty]
    private string _activeGrnNumber = string.Empty;

    partial void OnSelectedPurchaseOrderChanged(PurchaseOrder? value) => _ = LoadPoAsync(value);

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ProcessGoodsReceipt);
            PurchaseOrders.Clear();
            foreach (var po in await _grnService.GetReceivablePurchaseOrdersAsync().ConfigureAwait(true))
            {
                PurchaseOrders.Add(po);
            }

            RecentGrns.Clear();
            foreach (var grn in await _grnService.GetRecentGrnsAsync().ConfigureAwait(true))
            {
                RecentGrns.Add(grn);
            }

            StatusMessage = $"Loaded {PurchaseOrders.Count} receivable PO(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void ScanBarcode()
    {
        if (_draft is null)
        {
            StatusMessage = "Load a purchase order first.";
            return;
        }

        if (!_grnService.TryScanBarcode(_draft, BarcodeScanInput))
        {
            StatusMessage = $"No GRN line matches barcode '{BarcodeScanInput}'.";
            return;
        }

        SyncLinesFromDraft();
        StatusMessage = $"Scanned {BarcodeScanInput} — quantity updated.";
        BarcodeScanInput = string.Empty;
    }

    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        if (IsProcessingGrn || _draft is null)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ProcessGoodsReceipt);
            IsProcessingGrn = true;
            ApplyHeaderToDraft();
            SyncDraftFromUi();
            var result = await _grnService.SaveDraftAsync(_draft).ConfigureAwait(true);
            ActiveGrnNumber = result.GrnNumber;
            StatusMessage = result.Message;
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsProcessingGrn = false;
        }
    }

    [RelayCommand]
    private async Task PostGrnAsync()
    {
        if (IsProcessingGrn || _draft is null)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ProcessGoodsReceipt);
            IsProcessingGrn = true;
            ApplyHeaderToDraft();
            SyncDraftFromUi();
            var result = await _grnService.PostAsync(_draft).ConfigureAwait(true);
            StatusMessage = result.Message;
            ActiveGrnNumber = result.GrnNumber;
            if (result.Success)
            {
                SyncLinesFromDraft();
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsProcessingGrn = false;
        }
    }

    [RelayCommand]
    private void OpenReconciliation() =>
        _navigationService.NavigateTo<SupplierInvoiceReconciliationViewModel>();

    private async Task LoadPoAsync(PurchaseOrder? order)
    {
        ReceivedItemsList.Clear();
        _draft = null;
        ActiveGrnNumber = string.Empty;
        if (order is null)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ProcessGoodsReceipt);
            var lines = await _grnService.GetPurchaseOrderLinesAsync(order.PoId).ConfigureAwait(true);
            _draft = _grnService.CreateDraftFromPurchaseOrder(order, lines);
            _draft.OperatorUsername = _auth.CurrentOperator?.Username;
            ActiveGrnNumber = _draft.GrnNumber;
            SyncLinesFromDraft();
            StatusMessage = $"Loaded PO {order.PoNumber} with {_draft.Lines.Count} line(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void ApplyHeaderToDraft()
    {
        if (_draft is null)
        {
            return;
        }

        _draft.DeliveryNoteNumber = string.IsNullOrWhiteSpace(DeliveryNoteNumber) ? null : DeliveryNoteNumber.Trim();
        _draft.SupplierInvoiceNumber =
            string.IsNullOrWhiteSpace(SupplierInvoiceNumber) ? null : SupplierInvoiceNumber.Trim();
        _draft.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
        _draft.OperatorUsername = _auth.CurrentOperator?.Username;
    }

    private void SyncDraftFromUi()
    {
        if (_draft is null)
        {
            return;
        }

        // UI edits bind to the same GoodsReceiptLine instances in ReceivedItemsList.
        _draft.Lines = ReceivedItemsList.ToList();
    }

    private void SyncLinesFromDraft()
    {
        ReceivedItemsList.Clear();
        if (_draft is null)
        {
            return;
        }

        foreach (var line in _draft.Lines)
        {
            ReceivedItemsList.Add(line);
        }
    }
}
