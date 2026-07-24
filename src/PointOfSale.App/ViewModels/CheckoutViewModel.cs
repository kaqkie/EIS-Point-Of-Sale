using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;

namespace PointOfSale.App.ViewModels;

public partial class CheckoutViewModel : ObservableObject
{
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly OfflineSalesQueueService _offlineSalesQueueService;
    private readonly IOfflineInvoiceQueueRepository _queueRepository;
    private readonly IPosConfigurationService _posConfigurationService;
    private readonly IReceiptPrintingService _receiptPrintingService;
    private readonly IConnectionStatusService _connectionStatusService;
    private readonly INavigationService _navigationService;
    private readonly IProductionSecretGuard _productionSecretGuard;
    private readonly IPricingRulesEngine _pricingRulesEngine;
    private readonly ILoyaltyProgramService _loyaltyProgramService;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly ISupervisorAuthorizationService _supervisorAuthorization;
    private readonly ISupervisorOverrideDialogService _supervisorDialog;

    private ReceiptPrintRequest? _lastPrintableReceipt;

    public CheckoutViewModel(
        ILocalInventoryRepository inventoryRepository,
        OfflineSalesQueueService offlineSalesQueueService,
        IOfflineInvoiceQueueRepository queueRepository,
        IPosConfigurationService posConfigurationService,
        IReceiptPrintingService receiptPrintingService,
        IConnectionStatusService connectionStatusService,
        INavigationService navigationService,
        IProductionSecretGuard productionSecretGuard,
        IPricingRulesEngine pricingRulesEngine,
        ILoyaltyProgramService loyaltyProgramService,
        IAuthenticationAuthorizationService auth,
        ISupervisorAuthorizationService supervisorAuthorization,
        ISupervisorOverrideDialogService supervisorDialog)
    {
        _inventoryRepository = inventoryRepository;
        _offlineSalesQueueService = offlineSalesQueueService;
        _queueRepository = queueRepository;
        _posConfigurationService = posConfigurationService;
        _receiptPrintingService = receiptPrintingService;
        _connectionStatusService = connectionStatusService;
        _navigationService = navigationService;
        _productionSecretGuard = productionSecretGuard;
        _pricingRulesEngine = pricingRulesEngine;
        _loyaltyProgramService = loyaltyProgramService;
        _auth = auth;
        _supervisorAuthorization = supervisorAuthorization;
        _supervisorDialog = supervisorDialog;
        CartItems = new ObservableCollection<CartLineViewModel>();
        TaxLines = new ObservableCollection<TaxLineViewModel>();
        ActivePromotions = new ObservableCollection<string>();
        _ = LoadProductsAsync();
        _ = RefreshQueueBadgeAsync();
    }

    public ObservableCollection<CartLineViewModel> CartItems { get; }
    public ObservableCollection<TaxLineViewModel> TaxLines { get; }
    public ObservableCollection<string> ActivePromotions { get; }
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

    /// <summary>Text-box facing tender amount so typing updates change without decimal parse failures.</summary>
    [ObservableProperty]
    private string _amountTenderedText = "0.00";

    /// <summary>True while the on-screen numeric keypad is editing tender (avoids N2 reformat fighting keystrokes).</summary>
    private bool _keypadEditing;

    [ObservableProperty]
    private CartLineViewModel? _selectedCartLine;

    [ObservableProperty]
    private bool _isCashRegisterMode;

    [ObservableProperty]
    private bool _showAdvancedCheckoutTools = true;

    [ObservableProperty]
    private string _statusMessage = "Ready — F2 Add · F5 Exact tender · F9 Reprint · F8 Queue · F12 Complete";

    partial void OnIsCashRegisterModeChanged(bool value)
    {
        ShowAdvancedCheckoutTools = !value;
        if (value)
        {
            // Cash Register uses the same CompleteSale / tax / print pipeline — lock tender UI to cash.
            PaymentMethod = "Cash";
        }

        StatusMessage = value
            ? "Cash Register — F2 Add · F5 Exact · F12 Complete (receipt prints on success)"
            : "Ready — F2 Add · F5 Exact tender · F9 Reprint · F8 Queue · F12 Complete";
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _pendingQueueCount;

    [ObservableProperty]
    private int _quarantinedQueueCount;

    [ObservableProperty]
    private string _loyaltyMemberCode = string.Empty;

    [ObservableProperty]
    private LoyaltyMember? _attachedMember;

    [ObservableProperty]
    private decimal _availablePoints;

    [ObservableProperty]
    private decimal _pointsToRedeem;

    [ObservableProperty]
    private decimal _loyaltyDiscountMwk;

    [ObservableProperty]
    private decimal _promoDiscountTotal;

    /// <summary>Change owed to the customer: Amount Tendered − Grand Total (never negative).</summary>
    public decimal ChangeDue => Math.Max(0m, AmountTendered - CartGrandTotal);

    /// <summary>Cash still needed when tender is below the grand total.</summary>
    public decimal TenderShortfall => Math.Max(0m, CartGrandTotal - AmountTendered);

    public bool IsCashPayment =>
        string.Equals(PaymentMethod, "Cash", StringComparison.OrdinalIgnoreCase);

    public bool HasSufficientTender =>
        CartItems.Count > 0 && AmountTendered >= CartGrandTotal && CartGrandTotal > 0m;

    public bool HasInsufficientTender =>
        CartItems.Count > 0 && CartGrandTotal > 0m && AmountTendered < CartGrandTotal;

    public string TenderStatusMessage
    {
        get
        {
            if (CartItems.Count == 0 || CartGrandTotal <= 0m)
            {
                return "Add items to begin cash tender.";
            }

            if (HasInsufficientTender)
            {
                return $"Short by {TenderShortfall:N2} — enter cash handed over or press Exact (F5).";
            }

            return $"Change due: {ChangeDue:N2}";
        }
    }

    public bool CanReprintLastReceipt => _lastPrintableReceipt is not null;

    public IReadOnlyList<string> PaymentMethodOptions { get; } = ["Cash", "Card", "MobileMoney"];

    public IEnumerable<LocalInventoryItem> FilteredProducts =>
        string.IsNullOrWhiteSpace(SearchText)
            ? Products
            : Products.Where(p =>
                p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                p.ProductCode.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                (p.HsCode?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false));

    public int FilteredProductCount => FilteredProducts.Count();

    public string InventorySearchHint =>
        string.IsNullOrWhiteSpace(SearchText)
            ? $"Showing all {Products.Count} products — type to filter by name or code"
            : $"{FilteredProductCount} match(es) for \"{SearchText}\"";

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(FilteredProducts));
        OnPropertyChanged(nameof(FilteredProductCount));
        OnPropertyChanged(nameof(InventorySearchHint));
    }

    partial void OnAmountTenderedChanged(decimal value)
    {
        if (_keypadEditing)
        {
            NotifyTenderDerived();
            return;
        }

        var formatted = value.ToString("N2", CultureInfo.CurrentCulture);
        if (!string.Equals(AmountTenderedText, formatted, StringComparison.Ordinal))
        {
            // Keep the text box in sync when Exact / split / reset updates the decimal.
            var typedParses =
                decimal.TryParse(AmountTenderedText, NumberStyles.Number, CultureInfo.CurrentCulture, out var typed)
                || decimal.TryParse(AmountTenderedText, NumberStyles.Number, CultureInfo.InvariantCulture, out typed);
            if (!typedParses || typed != value)
            {
                AmountTenderedText = formatted;
            }
        }

        NotifyTenderDerived();
    }

    partial void OnAmountTenderedTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (AmountTendered != 0m)
            {
                AmountTendered = 0m;
            }
            else
            {
                NotifyTenderDerived();
            }

            return;
        }

        var trimmed = value.Trim();
        // Allow incomplete decimals while typing (e.g. "12." / "12,") without resetting the field.
        if (trimmed is "." or ","
            || trimmed.EndsWith('.')
            || trimmed.EndsWith(','))
        {
            NotifyTenderDerived();
            return;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount)
            || decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
        {
            if (amount < 0m)
            {
                amount = 0m;
            }

            if (AmountTendered != amount)
            {
                AmountTendered = amount;
                return;
            }
        }

        NotifyTenderDerived();
    }

    partial void OnCartGrandTotalChanged(decimal value) => NotifyTenderDerived();

    partial void OnPaymentMethodChanged(string value)
    {
        OnPropertyChanged(nameof(IsCashPayment));
        // Non-cash tenders settle at exact total; cash keeps the cashier-entered amount.
        if (!IsCashPayment && CartGrandTotal > 0m)
        {
            AmountTendered = CartGrandTotal;
            AmountTenderedText = CartGrandTotal.ToString("N2", CultureInfo.CurrentCulture);
        }

        NotifyTenderDerived();
    }

    private void NotifyTenderDerived()
    {
        OnPropertyChanged(nameof(ChangeDue));
        OnPropertyChanged(nameof(TenderShortfall));
        OnPropertyChanged(nameof(HasSufficientTender));
        OnPropertyChanged(nameof(HasInsufficientTender));
        OnPropertyChanged(nameof(TenderStatusMessage));
        OnPropertyChanged(nameof(IsCashPayment));
        CompleteSaleCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task LoadProductsAsync()
    {
        Products.Clear();
        var items = await _inventoryRepository.GetAllAsync().ConfigureAwait(true);
        foreach (var item in items)
        {
            Products.Add(item);
        }

        OnPropertyChanged(nameof(FilteredProducts));
        OnPropertyChanged(nameof(FilteredProductCount));
        OnPropertyChanged(nameof(InventorySearchHint));
    }

    [RelayCommand]
    private void AddSelectedToCart()
    {
        if (SelectedProduct is null)
        {
            StatusMessage = "Select a product first (or search and press F2).";
            return;
        }

        AddProductToCart(SelectedProduct, 1);
        StatusMessage = $"Added {SelectedProduct.Name}.";
    }

    [RelayCommand]
    private async Task RemoveCartLineAsync(CartLineViewModel? line)
    {
        if (line is null)
        {
            return;
        }

        if (!_auth.HasPermission(OperatorPermissions.PerformVoid))
        {
            var overrideResult = await _supervisorDialog.PromptAsync(
                    new SupervisorOverrideRequest
                    {
                        ActionType = SupervisorOverrideActions.ItemVoid,
                        RequiredPermission = OperatorPermissions.PerformVoid,
                        Reason = $"Authorize void of '{line.Description}' ({line.ProductCode}).",
                        Detail = $"qty={line.Quantity}; lineTotal={line.LineTotal:N2}"
                    })
                .ConfigureAwait(true);

            if (!overrideResult.Authorized)
            {
                StatusMessage = overrideResult.Error ?? "Item void denied — supervisor authorization required.";
                return;
            }

            StatusMessage = $"Void authorized by {overrideResult.AuthorizingUsername}.";
        }
        else
        {
            await _supervisorAuthorization.AuthorizeAsync(
                    new SupervisorOverrideRequest
                    {
                        ActionType = SupervisorOverrideActions.ItemVoid,
                        RequiredPermission = OperatorPermissions.PerformVoid,
                        Reason = $"Session void of '{line.Description}'.",
                        AllowCurrentSession = true
                    })
                .ConfigureAwait(true);
        }

        CartItems.Remove(line);
        RecalculateTotals();
    }

    [RelayCommand]
    private void TenderExactAmount()
    {
        if (CartGrandTotal <= 0)
        {
            StatusMessage = "Cart is empty.";
            return;
        }

        _keypadEditing = false;
        AmountTendered = CartGrandTotal;
        AmountTenderedText = CartGrandTotal.ToString("N2", CultureInfo.CurrentCulture);
        StatusMessage = $"Exact tender set to {CartGrandTotal:N2}. Change due: {ChangeDue:N2}.";
    }

    [RelayCommand]
    private void AddQuickTender(string? denomination)
    {
        if (!decimal.TryParse(denomination, NumberStyles.Number, CultureInfo.InvariantCulture, out var add)
            && !decimal.TryParse(denomination, NumberStyles.Number, CultureInfo.CurrentCulture, out add))
        {
            return;
        }

        if (add <= 0m)
        {
            return;
        }

        _keypadEditing = false;
        AmountTendered = PosTaxCalculator.RoundMoney(AmountTendered + add);
        AmountTenderedText = AmountTendered.ToString("N2", CultureInfo.CurrentCulture);
        StatusMessage = HasInsufficientTender
            ? $"Tendered {AmountTendered:N2} — short by {TenderShortfall:N2}."
            : $"Tendered {AmountTendered:N2} — change due {ChangeDue:N2}.";
    }

    [RelayCommand]
    private void KeypadPress(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        PaymentMethod = "Cash";
        _keypadEditing = true;

        var buffer = AmountTenderedText?.Trim() ?? string.Empty;
        if (buffer is "0.00" or "0,00" or "0" or "0.0" or "0,0")
        {
            buffer = string.Empty;
        }

        if (key is "." or ",")
        {
            var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            if (buffer.Contains('.', StringComparison.Ordinal) || buffer.Contains(',', StringComparison.Ordinal))
            {
                return;
            }

            AmountTenderedText = string.IsNullOrEmpty(buffer) ? "0" + sep : buffer + sep;
            return;
        }

        if (key.Length == 1 && char.IsDigit(key[0]))
        {
            var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
            var sepIndex = buffer.IndexOf(sep, StringComparison.Ordinal);
            if (sepIndex >= 0 && buffer.Length - sepIndex > 2)
            {
                return; // max 2 decimal places
            }

            AmountTenderedText = buffer + key;
        }
    }

    [RelayCommand]
    private void KeypadBackspace()
    {
        PaymentMethod = "Cash";
        _keypadEditing = true;
        var buffer = AmountTenderedText ?? string.Empty;
        if (buffer.Length <= 1)
        {
            AmountTenderedText = "0";
            return;
        }

        AmountTenderedText = buffer[..^1];
    }

    [RelayCommand]
    private void KeypadClear()
    {
        PaymentMethod = "Cash";
        _keypadEditing = true;
        AmountTenderedText = "0";
        AmountTendered = 0m;
        StatusMessage = "Cash tender cleared — enter amount on keypad.";
    }

    [RelayCommand]
    private void KeypadConfirmTender()
    {
        PaymentMethod = "Cash";
        _keypadEditing = false;
        AmountTenderedText = AmountTendered.ToString("N2", CultureInfo.CurrentCulture);
        if (CartGrandTotal <= 0m)
        {
            StatusMessage = "Add items before tendering cash.";
            return;
        }

        StatusMessage = HasInsufficientTender
            ? $"Tendered {AmountTendered:N2} — short by {TenderShortfall:N2}. Enter more cash or Exact (F5)."
            : $"Tender OK — {AmountTendered:N2} received. Change due: {ChangeDue:N2}. Press Paid to complete.";
    }

    [RelayCommand]
    private void SelectPaymentMethod(string? method)
    {
        var normalized = method?.Trim() switch
        {
            "Credit" or "Other Card" or "Card" => "Card",
            "Gift Card" => "MobileMoney",
            "MobileMoney" => "MobileMoney",
            _ => "Cash"
        };

        PaymentMethod = normalized;
        _keypadEditing = false;

        if (IsCashPayment)
        {
            StatusMessage = "Cash selected — type amount on the keypad, then OK / Tender.";
            return;
        }

        if (CartGrandTotal > 0m)
        {
            AmountTendered = CartGrandTotal;
            AmountTenderedText = CartGrandTotal.ToString("N2", CultureInfo.CurrentCulture);
        }

        StatusMessage = $"{normalized} selected — tender set to exact total {CartGrandTotal:N2}.";
    }

    [RelayCommand]
    private async Task VoidSelectedCartLineAsync()
    {
        if (SelectedCartLine is null)
        {
            StatusMessage = "Select a cart line to void.";
            return;
        }

        await RemoveCartLineAsync(SelectedCartLine).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task AttachLoyaltyMemberAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.LookupLoyaltyCustomer);
            if (string.IsNullOrWhiteSpace(LoyaltyMemberCode))
            {
                StatusMessage = "Enter a loyalty member code.";
                return;
            }

            var member = await _loyaltyProgramService.GetByCodeAsync(LoyaltyMemberCode).ConfigureAwait(true);
            if (member is null || !member.IsActive)
            {
                StatusMessage = "Loyalty member not found.";
                AttachedMember = null;
                AvailablePoints = 0;
                return;
            }

            AttachedMember = member;
            AvailablePoints = member.PointsBalance;
            StatusMessage = $"Attached {member.FullName} — {member.PointsBalance:N2} pts.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ApplyPromotionsAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ExecuteCheckout);
            if (CartItems.Count == 0)
            {
                StatusMessage = "Cart is empty.";
                return;
            }

            var lines = CartItems.Select(c => new PricingCartLine
            {
                ProductCode = c.ProductCode,
                Description = c.Description,
                CategoryCode = c.TaxRateId,
                UnitPrice = c.UnitPrice,
                Quantity = c.Quantity,
                VatRatePercent = c.VatRatePercent
            }).ToList();

            var result = await _pricingRulesEngine.EvaluateAsync(lines).ConfigureAwait(true);
            foreach (var item in CartItems)
            {
                var adj = result.LineAdjustments.FirstOrDefault(a =>
                    a.ProductCode.Equals(item.ProductCode, StringComparison.OrdinalIgnoreCase));
                item.PromoDiscountNet = adj?.DiscountNet ?? 0m;
                item.AppliedPromotion = adj?.AppliedRuleName;
            }

            ActivePromotions.Clear();
            foreach (var name in result.AppliedPromotionNames)
            {
                ActivePromotions.Add(name);
            }

            RecalculateTotals();
            StatusMessage = result.TotalDiscountNet <= 0
                ? "No active promotions matched this cart."
                : $"Applied promotions — discount net {result.TotalDiscountNet:N2}.";
        }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "Checkout permission required to apply promotions.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void PreviewLoyaltyRedeem()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.RedeemLoyaltyPoints);
            if (AttachedMember is null)
            {
                StatusMessage = "Attach a loyalty member first.";
                return;
            }

            if (PointsToRedeem > AttachedMember.PointsBalance)
            {
                StatusMessage = "Cannot redeem more points than available.";
                return;
            }

            LoyaltyDiscountMwk = _loyaltyProgramService.CalculateRedeemValueMwk(PointsToRedeem);
            RecalculateTotals();
            StatusMessage = $"Loyalty tender discount preview {LoyaltyDiscountMwk:N2} MWK.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenQueueStatus()
    {
        _navigationService.NavigateTo<QueueSyncStatusViewModel>();
    }

    [RelayCommand]
    private async Task ReprintLastReceiptAsync()
    {
        if (_lastPrintableReceipt is null)
        {
            ShowOperatorDialog(new OperatorMessage(
                "Nothing to reprint",
                "Complete an online fiscalized sale first, then press F9 to reprint the last receipt.",
                OperatorMessageSeverity.Information,
                SuggestOfflineFallback: false));
            return;
        }

        try
        {
            await _receiptPrintingService.PrintAsync(_lastPrintableReceipt).ConfigureAwait(true);
            StatusMessage = $"Reprinted invoice {_lastPrintableReceipt.InvoiceNumber}.";
        }
        catch (Exception ex)
        {
            var message = CashierOperatorMessages.FromException(ex, _connectionStatusService.IsMraReachable);
            ShowOperatorDialog(message);
            StatusMessage = message.Title;
        }
    }

    private bool CanCompleteSale() =>
        !IsBusy && CartItems.Count > 0 && AmountTendered >= CartGrandTotal;

    [RelayCommand(CanExecute = nameof(CanCompleteSale))]
    private async Task CompleteSaleAsync()
    {
        if (CartItems.Count == 0)
        {
            StatusMessage = "Cart is empty.";
            return;
        }

        if (AmountTendered < CartGrandTotal)
        {
            ShowOperatorDialog(new OperatorMessage(
                "Insufficient tender",
                $"Cash tendered ({AmountTendered:N2}) is less than the total ({CartGrandTotal:N2}). Short by {TenderShortfall:N2}. Enter the cash handed over or press Exact (F5).",
                OperatorMessageSeverity.Warning,
                SuggestOfflineFallback: false));
            StatusMessage = TenderStatusMessage;
            return;
        }

        IsBusy = true;
        CompleteSaleCommand.NotifyCanExecuteChanged();
        try
        {
            await _productionSecretGuard.EnsureReadyForLiveSalesAsync().ConfigureAwait(true);

            var context = await _posConfigurationService.GetRuntimeContextAsync().ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(context.SellerTin) || string.IsNullOrWhiteSpace(context.SiteId))
            {
                ShowOperatorDialog(new OperatorMessage(
                    "Terminal configuration incomplete",
                    "Run onboarding, sync MRA configs, and set Branch/Site IDs in appsettings.Production.json before selling.",
                    OperatorMessageSeverity.Error,
                    SuggestOfflineFallback: false));
                StatusMessage = "Terminal configuration incomplete.";
                return;
            }

            var invoiceNumber = $"ART-{DateTime.Now:yyyyMMddHHmmss}";

            // Refresh probe with the full EIS timeout before deciding offline.
            await _connectionStatusService.RefreshAsync().ConfigureAwait(true);

            // Only force offline when there is no network at all. A failed MRA probe must not
            // skip the authenticated sales submit — probes can false-negative (HEAD/GET on /api/v1/).
            var forceOffline = false;
            if (!_connectionStatusService.IsOnline)
            {
                var proceed = ConfirmOfflineFallback();
                if (!proceed)
                {
                    StatusMessage = "Sale cancelled — waiting for network connectivity.";
                    return;
                }

                forceOffline = true;
            }
            else if (!_connectionStatusService.IsMraReachable)
            {
                // Network is up; attempt live EIS submit. Offline is only offered if submit fails.
                StatusMessage = "MRA probe degraded — attempting live EIS submit…";
            }

            // Atomically redeem loyalty points before fiscal submit when requested.
            if (AttachedMember is not null && PointsToRedeem > 0 && LoyaltyDiscountMwk > 0)
            {
                _auth.EnsurePermission(OperatorPermissions.RedeemLoyaltyPoints);
                var redeem = await _loyaltyProgramService.RedeemAtCheckoutAsync(
                        AttachedMember.MemberId,
                        PointsToRedeem,
                        invoiceNumber)
                    .ConfigureAwait(true);
                if (!redeem.Success)
                {
                    StatusMessage = redeem.Error ?? "Loyalty redemption failed.";
                    return;
                }

                LoyaltyDiscountMwk = redeem.DiscountMwk;
                AvailablePoints = redeem.NewBalance;
                RecalculateTotals();
            }

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

            var result = await _offlineSalesQueueService
                .EnqueueAndTrySubmitAsync(request, forceOffline)
                .ConfigureAwait(true);

            if (result.IsQuarantined)
            {
                var message = CashierOperatorMessages.Quarantined(result.Remark);
                ShowOperatorDialog(message);
                StatusMessage = message.Title;
                await RefreshQueueBadgeAsync().ConfigureAwait(true);
                return;
            }

            if (result.SubmittedOnline && result.Response is not null)
            {
                var fiscal = FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
                    result.Response,
                    result.InvoiceNumber);
                await PrintReceiptAsync(request, fiscal).ConfigureAwait(true);
                var ok = CashierOperatorMessages.SubmittedOnline(result.InvoiceNumber);
                StatusMessage = $"{ok.Body} Receipt sent to printer.";
            }
            else
            {
                var queued = CashierOperatorMessages.QueuedOffline(result.InvoiceNumber, forceOffline);
                ShowOperatorDialog(queued);
                // Same cash-register / POS path: always print a customer receipt after a successful take.
                await PrintReceiptAsync(
                        request,
                        FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
                            new SubmitSalesTransactionResponseData
                            {
                                InvoiceNumber = result.InvoiceNumber,
                                FiscalSignature = request.InvoiceSummary.OfflineSignature
                                    ?? FiscalReceiptEnricher.OfflinePendingPlaceholder
                            },
                            result.InvoiceNumber))
                    .ConfigureAwait(true);
                StatusMessage = $"{queued.Body} Receipt sent to printer.";
            }

            // Capture before cart reset — earn points on the final paid invoice total.
            var earnMemberId = AttachedMember?.MemberId;
            var earnInvoiceTotal = CartGrandTotal;
            if (earnMemberId is int memberId)
            {
                await _loyaltyProgramService.EarnFromPurchaseAsync(
                        memberId,
                        earnInvoiceTotal,
                        result.InvoiceNumber)
                    .ConfigureAwait(true);
            }

            CartItems.Clear();
            ActivePromotions.Clear();
            AttachedMember = null;
            AvailablePoints = 0;
            PointsToRedeem = 0;
            LoyaltyDiscountMwk = 0;
            PromoDiscountTotal = 0;
            RecalculateTotals();
            _keypadEditing = false;
            AmountTendered = 0;
            AmountTenderedText = "0.00";
            await RefreshQueueBadgeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var message = CashierOperatorMessages.FromException(ex, _connectionStatusService.IsMraReachable);
            if (message.SuggestOfflineFallback && CartItems.Count > 0)
            {
                var retryOffline = ConfirmOfflineFallback(message);
                if (retryOffline)
                {
                    await TryForceOfflineSaleAsync().ConfigureAwait(true);
                    return;
                }
            }

            ShowOperatorDialog(message);
            StatusMessage = message.Title;
        }
        finally
        {
            IsBusy = false;
            CompleteSaleCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task TryForceOfflineSaleAsync()
    {
        try
        {
            var context = await _posConfigurationService.GetRuntimeContextAsync().ConfigureAwait(true);
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

            var result = await _offlineSalesQueueService
                .EnqueueAndTrySubmitAsync(request, forceOffline: true)
                .ConfigureAwait(true);

            var queued = CashierOperatorMessages.QueuedOffline(result.InvoiceNumber, forcedOffline: true);
            ShowOperatorDialog(queued);
            await PrintReceiptAsync(
                    request,
                    FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
                        new SubmitSalesTransactionResponseData
                        {
                            InvoiceNumber = result.InvoiceNumber,
                            FiscalSignature = request.InvoiceSummary.OfflineSignature
                                ?? FiscalReceiptEnricher.OfflinePendingPlaceholder
                        },
                        result.InvoiceNumber))
                .ConfigureAwait(true);
            StatusMessage = $"{queued.Body} Receipt sent to printer.";
            CartItems.Clear();
            RecalculateTotals();
            _keypadEditing = false;
            AmountTendered = 0;
            AmountTenderedText = "0.00";
            await RefreshQueueBadgeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            var message = CashierOperatorMessages.FromException(ex, mraReachable: false);
            ShowOperatorDialog(message);
            StatusMessage = message.Title;
        }
    }

    private async Task RefreshQueueBadgeAsync()
    {
        try
        {
            var counts = await _queueRepository.GetStatusCountsAsync().ConfigureAwait(true);
            PendingQueueCount = counts.GetValueOrDefault(Core.Constants.OfflineQueueStatuses.Pending)
                + counts.GetValueOrDefault(Core.Constants.OfflineQueueStatuses.Syncing);
            QuarantinedQueueCount = counts.GetValueOrDefault(Core.Constants.OfflineQueueStatuses.Quarantined);
        }
        catch
        {
            // Badge is advisory only.
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
        // Clear loyalty shares then allocate proportionally across promo-adjusted nets.
        foreach (var line in CartItems)
        {
            line.LoyaltyShareNet = 0m;
            line.RefreshTotals();
        }

        var netsAfterPromo = CartItems.Sum(x => x.NetBeforeLoyalty);
        if (LoyaltyDiscountMwk > 0 && netsAfterPromo > 0 && CartItems.Count > 0)
        {
            var remaining = LoyaltyDiscountMwk;
            for (var i = 0; i < CartItems.Count; i++)
            {
                var line = CartItems[i];
                decimal share;
                if (i == CartItems.Count - 1)
                {
                    share = remaining;
                }
                else
                {
                    share = PosTaxCalculator.RoundMoney(LoyaltyDiscountMwk * (line.NetBeforeLoyalty / netsAfterPromo));
                    share = Math.Min(share, line.NetBeforeLoyalty);
                    remaining = PosTaxCalculator.RoundMoney(remaining - share);
                }

                line.LoyaltyShareNet = Math.Min(share, line.NetBeforeLoyalty);
                line.RefreshTotals();
            }
        }

        PromoDiscountTotal = CartItems.Sum(x => x.PromoDiscountNet);
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
        var fiscal = FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
            response,
            request.InvoiceHeader.InvoiceNumber);
        var printRequest = new ReceiptPrintRequest
        {
            TradingName = context.TradingName,
            SellerTin = context.SellerTin,
            AddressLines = context.AddressLines,
            InvoiceNumber = request.InvoiceHeader.InvoiceNumber,
            InvoiceDateTime = request.InvoiceHeader.InvoiceDateTime,
            LineItems = request.InvoiceLineItems,
            TaxBreakdown = request.InvoiceSummary.TaxBreakDown,
            SubtotalNet = request.InvoiceSummary.InvoiceTotal - request.InvoiceSummary.TotalVat,
            TotalVat = request.InvoiceSummary.TotalVat,
            InvoiceTotal = request.InvoiceSummary.InvoiceTotal,
            AmountTendered = request.InvoiceSummary.AmountTendered,
            FiscalResponse = fiscal
        };

        _lastPrintableReceipt = printRequest;
        OnPropertyChanged(nameof(CanReprintLastReceipt));
        await _receiptPrintingService.PrintAsync(printRequest).ConfigureAwait(true);
    }

    private static bool ConfirmOfflineFallback(OperatorMessage? preface = null)
    {
        var body = preface is null
            ? "No network connection. Save this sale to the offline queue and sync later?"
            : $"{preface.Body}\n\nSave this sale to the offline queue instead?";

        var result = MessageBox.Show(
            Application.Current.MainWindow,
            body,
            preface?.Title ?? "Offline fallback",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    private static void ShowOperatorDialog(OperatorMessage message)
    {
        var icon = message.Severity switch
        {
            OperatorMessageSeverity.Warning => MessageBoxImage.Warning,
            OperatorMessageSeverity.Error => MessageBoxImage.Error,
            _ => MessageBoxImage.Information
        };

        MessageBox.Show(
            Application.Current.MainWindow,
            message.Body,
            message.Title,
            MessageBoxButton.OK,
            icon);
    }
}

public partial class CartLineViewModel : ObservableObject
{
    [ObservableProperty]
    private decimal _quantity;

    [ObservableProperty]
    private decimal _promoDiscountNet;

    [ObservableProperty]
    private decimal _loyaltyShareNet;

    [ObservableProperty]
    private string? _appliedPromotion;

    public required string ProductCode { get; init; }
    public required string Description { get; init; }
    public required string TaxRateId { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal VatRatePercent { get; init; }

    public decimal GrossNet => PosTaxCalculator.CalculateNetAmount(UnitPrice, Quantity);
    public decimal NetBeforeLoyalty => PosTaxCalculator.RoundMoney(Math.Max(0m, GrossNet - PromoDiscountNet));
    public decimal TotalDiscountNet => PosTaxCalculator.RoundMoney(PromoDiscountNet + LoyaltyShareNet);

    public decimal NetTotal { get; private set; }
    public decimal VatTotal { get; private set; }
    public decimal LineTotal => NetTotal + VatTotal;

    public static CartLineViewModel FromProduct(LocalInventoryItem product, decimal quantity) =>
        new()
        {
            ProductCode = product.ProductCode,
            Description = product.Name,
            TaxRateId = product.TaxRateId ?? "T",
            UnitPrice = product.UnitPrice,
            VatRatePercent = PosTaxCalculator.MalawiStandardVatRatePercent,
            Quantity = quantity
        };

    public void RefreshTotals()
    {
        var mapped = PosTaxCalculator.ApplyNetDiscount(UnitPrice, Quantity, VatRatePercent, TotalDiscountNet);
        NetTotal = mapped.NetAfterDiscount;
        VatTotal = mapped.Vat;
        OnPropertyChanged(nameof(GrossNet));
        OnPropertyChanged(nameof(NetBeforeLoyalty));
        OnPropertyChanged(nameof(TotalDiscountNet));
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
            Discount = TotalDiscountNet,
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
