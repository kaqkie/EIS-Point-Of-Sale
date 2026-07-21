using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.App.ViewModels;

public partial class CheckoutViewModel : ObservableObject
{
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly OfflineSalesQueueService _offlineSalesQueueService;
    private readonly IPosConfigurationService _posConfigurationService;
    private readonly IReceiptPrintingService _receiptPrintingService;
    private readonly IConnectionStatusService _connectionStatusService;

    public CheckoutViewModel(
        ILocalInventoryRepository inventoryRepository,
        OfflineSalesQueueService offlineSalesQueueService,
        IPosConfigurationService posConfigurationService,
        IReceiptPrintingService receiptPrintingService,
        IConnectionStatusService connectionStatusService)
    {
        _inventoryRepository = inventoryRepository;
        _offlineSalesQueueService = offlineSalesQueueService;
        _posConfigurationService = posConfigurationService;
        _receiptPrintingService = receiptPrintingService;
        _connectionStatusService = connectionStatusService;
        CartItems = new ObservableCollection<CartLineViewModel>();
        TaxLines = new ObservableCollection<TaxLineViewModel>();
        _ = LoadProductsAsync();
    }

    public ObservableCollection<CartLineViewModel> CartItems { get; }
    public ObservableCollection<TaxLineViewModel> TaxLines { get; }
    public ObservableCollection<LocalInventoryItem> Products { get; } = new();

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private LocalInventoryItem? _selectedProduct;

    [ObservableProperty]
    private decimal _cartSubtotal;

    [ObservableProperty]
    private decimal _cartTaxTotal;

    [ObservableProperty]
    private decimal _cartGrandTotal;

    [ObservableProperty]
    private string _paymentMethod = "Cash";

    [ObservableProperty]
    private decimal _amountTendered;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private bool _isBusy;

    public IEnumerable<LocalInventoryItem> FilteredProducts =>
        string.IsNullOrWhiteSpace(SearchText)
            ? Products
            : Products.Where(p =>
                p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                p.ProductCode.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredProducts));

    [RelayCommand]
    private async Task LoadProductsAsync()
    {
        Products.Clear();
        var items = await _inventoryRepository.GetAllAsync().ConfigureAwait(true);
        foreach (var item in items)
        {
            Products.Add(item);
        }
    }

    [RelayCommand]
    private void AddSelectedToCart()
    {
        if (SelectedProduct is null)
        {
            return;
        }

        AddProductToCart(SelectedProduct, 1);
    }

    [RelayCommand]
    private void RemoveCartLine(CartLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        CartItems.Remove(line);
        RecalculateTotals();
    }

    [RelayCommand]
    private async Task CompleteSaleAsync()
    {
        if (CartItems.Count == 0)
        {
            StatusMessage = "Cart is empty.";
            return;
        }

        if (AmountTendered < CartGrandTotal)
        {
            StatusMessage = "Amount tendered is less than total.";
            return;
        }

        IsBusy = true;
        try
        {
            var context = await _posConfigurationService.GetRuntimeContextAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(context.SellerTin) || string.IsNullOrWhiteSpace(context.SiteId))
            {
                StatusMessage = "Terminal configuration incomplete. Run onboarding and sync configs.";
                return;
            }

            var invoiceNumber = $"ART-{DateTime.Now:yyyyMMddHHmmss}";
            var lineItems = CartItems.Select((x, index) => x.ToInvoiceLine(index + 1)).ToList();
            var request = new SubmitSalesTransactionRequest
            {
                InvoiceHeader = new InvoiceHeaderDto
                {
                    InvoiceNumber = invoiceNumber,
                    InvoiceDateTime = DateTime.UtcNow,
                    SellerTin = context.SellerTin,
                    SiteId = context.SiteId,
                    GlobalConfigVersion = context.GlobalConfigVersion,
                    TaxpayerConfigVersion = context.TaxpayerConfigVersion,
                    TerminalConfigVersion = context.TerminalConfigVersion,
                    PaymentMethod = PaymentMethod
                },
                InvoiceLineItems = lineItems,
                InvoiceSummary = new InvoiceSummaryDto
                {
                    TaxBreakDown = TaxLines.Select(t => new TaxBreakDownDto
                    {
                        RateId = t.RateId,
                        TaxableAmount = t.TaxableAmount,
                        TaxAmount = t.TaxAmount
                    }).ToList(),
                    TotalVat = CartTaxTotal,
                    InvoiceTotal = CartGrandTotal,
                    AmountTendered = AmountTendered
                }
            };

            var forceOffline = !_connectionStatusService.IsMraReachable;
            var result = await _offlineSalesQueueService
                .EnqueueAndTrySubmitAsync(request, forceOffline)
                .ConfigureAwait(true);

            if (result.IsQuarantined)
            {
                StatusMessage = $"Sale quarantined: {result.Remark}";
                return;
            }

            if (result.SubmittedOnline && result.Response is not null)
            {
                await PrintReceiptAsync(request, result.Response).ConfigureAwait(true);
                StatusMessage = $"Sale submitted online — invoice {result.InvoiceNumber}.";
            }
            else
            {
                StatusMessage = forceOffline
                    ? $"Sale queued offline — invoice {result.InvoiceNumber}."
                    : $"Sale queued for sync — invoice {result.InvoiceNumber}.";
            }

            CartItems.Clear();
            RecalculateTotals();
            AmountTendered = 0;
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

    private void AddProductToCart(LocalInventoryItem product, decimal quantity)
    {
        var existing = CartItems.FirstOrDefault(x =>
            x.ProductCode.Equals(product.ProductCode, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.Quantity += quantity;
            existing.RefreshTotals();
        }
        else
        {
            CartItems.Add(CartLineViewModel.FromProduct(product, quantity));
        }

        RecalculateTotals();
    }

    private void RecalculateTotals()
    {
        foreach (var line in CartItems)
        {
            line.RefreshTotals();
        }

        CartSubtotal = CartItems.Sum(x => x.NetTotal);
        CartTaxTotal = CartItems.Sum(x => x.VatTotal);
        CartGrandTotal = CartSubtotal + CartTaxTotal;

        TaxLines.Clear();
        foreach (var group in CartItems.GroupBy(x => x.TaxRateId))
        {
            TaxLines.Add(new TaxLineViewModel
            {
                RateId = group.Key,
                TaxableAmount = group.Sum(x => x.NetTotal),
                TaxAmount = group.Sum(x => x.VatTotal)
            });
        }
    }

    private async Task PrintReceiptAsync(
        SubmitSalesTransactionRequest request,
        SubmitSalesTransactionResponseData response)
    {
        var context = await _posConfigurationService.GetRuntimeContextAsync().ConfigureAwait(true);
        await _receiptPrintingService.PrintAsync(
            new ReceiptPrintRequest
            {
                TradingName = context.TradingName,
                SellerTin = context.SellerTin,
                AddressLines = context.AddressLines,
                InvoiceNumber = request.InvoiceHeader.InvoiceNumber,
                InvoiceDateTime = request.InvoiceHeader.InvoiceDateTime,
                LineItems = request.InvoiceLineItems,
                TaxBreakdown = request.InvoiceSummary.TaxBreakDown,
                InvoiceTotal = request.InvoiceSummary.InvoiceTotal,
                AmountTendered = request.InvoiceSummary.AmountTendered,
                FiscalResponse = response
            }).ConfigureAwait(true);
    }
}

public partial class CartLineViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _quantity;

    public required string ProductCode { get; init; }
    public required string Description { get; init; }
    public required string TaxRateId { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal VatRatePercent { get; init; }

    public decimal NetTotal => Math.Round(UnitPrice * Quantity, 2);
    public decimal VatTotal => Math.Round(NetTotal * (VatRatePercent / 100m), 2);
    public decimal LineTotal => NetTotal + VatTotal;

    public static CartLineViewModel FromProduct(LocalInventoryItem product, decimal quantity) =>
        new()
        {
            ProductCode = product.ProductCode,
            Description = product.Name,
            TaxRateId = product.TaxRateId ?? "T",
            UnitPrice = product.UnitPrice,
            VatRatePercent = 16.5m,
            Quantity = quantity
        };

    public void RefreshTotals()
    {
        OnPropertyChanged(nameof(NetTotal));
        OnPropertyChanged(nameof(VatTotal));
        OnPropertyChanged(nameof(LineTotal));
    }

    public InvoiceLineItemDto ToInvoiceLine(int id) =>
        new()
        {
            Id = id,
            ProductCode = ProductCode,
            Description = Description,
            UnitPrice = UnitPrice,
            Quantity = Quantity,
            Discount = 0,
            Total = NetTotal,
            TotalVat = VatTotal,
            TaxRateId = TaxRateId,
            IsProduct = true
        };
}

public partial class TaxLineViewModel : ObservableObject
{
    [ObservableProperty]
    private string _rateId = string.Empty;

    [ObservableProperty]
    private decimal _taxableAmount;

    [ObservableProperty]
    private decimal _taxAmount;
}
