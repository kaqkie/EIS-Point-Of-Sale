using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;

namespace PointOfSale.App.ViewModels;

public partial class BarcodePrintingViewModel : ObservableObject
{
    private readonly ILocalInventoryRepository _inventoryRepository;
    private readonly IBarcodeGenerationService _barcodeService;
    private readonly ILabelTemplateService _labelTemplateService;
    private readonly ILabelPrintBatchRepository _batchRepository;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly LabelPrintingOptions _options;
    private IReadOnlyList<LocalInventoryItem> _allProducts = Array.Empty<LocalInventoryItem>();

    public BarcodePrintingViewModel(
        ILocalInventoryRepository inventoryRepository,
        IBarcodeGenerationService barcodeService,
        ILabelTemplateService labelTemplateService,
        ILabelPrintBatchRepository batchRepository,
        IAuthenticationAuthorizationService auth,
        IOptions<LabelPrintingOptions> options)
    {
        _inventoryRepository = inventoryRepository;
        _barcodeService = barcodeService;
        _labelTemplateService = labelTemplateService;
        _batchRepository = batchRepository;
        _auth = auth;
        _options = options.Value;

        CatalogProducts = new ObservableCollection<LocalInventoryItem>();
        SelectedProductsList = new ObservableCollection<LocalInventoryItem>();
        RecentBatches = new ObservableCollection<LabelPrintBatch>();
        TemplateTypes = new ObservableCollection<string>(
            _labelTemplateService.GetTemplates().Select(t => t.TemplateType));

        SelectedTemplateType = TemplateTypes.Contains(_options.DefaultTemplateType)
            ? _options.DefaultTemplateType
            : LabelTemplateTypes.ShelfEdge50x30;
        QuantityPerItem = Math.Max(1, _options.DefaultQuantityPerItem);
        _ = InitializeAsync();
    }

    public ObservableCollection<LocalInventoryItem> CatalogProducts { get; }
    public ObservableCollection<LocalInventoryItem> SelectedProductsList { get; }
    public ObservableCollection<LabelPrintBatch> RecentBatches { get; }
    public ObservableCollection<string> TemplateTypes { get; }

    [ObservableProperty]
    private string _productSearchQuery = string.Empty;

    [ObservableProperty]
    private LocalInventoryItem? _selectedCatalogProduct;

    [ObservableProperty]
    private LocalInventoryItem? _selectedBatchProduct;

    [ObservableProperty]
    private string _selectedTemplateType = LabelTemplateTypes.ShelfEdge50x30;

    [ObservableProperty]
    private int _quantityPerItem = 1;

    [ObservableProperty]
    private bool _isPrinting;

    [ObservableProperty]
    private string _statusMessage = "Select products and print shelf-edge / barcode labels.";

    [ObservableProperty]
    private string _activeTemplateDescription = string.Empty;

    [ObservableProperty]
    private BitmapSource? _previewBarcodeImage;

    [ObservableProperty]
    private string _previewPriceLine = string.Empty;

    [ObservableProperty]
    private int _estimatedLabelCount;

    partial void OnProductSearchQueryChanged(string value) => ApplyCatalogFilter();

    partial void OnSelectedTemplateTypeChanged(string value)
    {
        var template = _labelTemplateService.GetTemplate(value);
        ActiveTemplateDescription = $"{template.DisplayName} — {template.Description}";
        RefreshPreview();
    }

    partial void OnQuantityPerItemChanged(int value)
    {
        EstimatedLabelCount = Math.Max(0, SelectedProductsList.Count * Math.Max(1, value));
    }

    partial void OnSelectedBatchProductChanged(LocalInventoryItem? value) => RefreshPreview();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.PrintProductLabels);
            _allProducts = await _inventoryRepository.GetAllAsync().ConfigureAwait(true);
            ApplyCatalogFilter();
            await LoadBatchesAsync().ConfigureAwait(true);
            StatusMessage = $"Loaded {_allProducts.Count} product(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void AddSelectedProduct()
    {
        if (SelectedCatalogProduct is null)
        {
            StatusMessage = "Select a catalog product first.";
            return;
        }

        if (SelectedProductsList.Any(p =>
                p.ProductCode.Equals(SelectedCatalogProduct.ProductCode, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"{SelectedCatalogProduct.ProductCode} is already in the batch.";
            return;
        }

        SelectedProductsList.Add(SelectedCatalogProduct);
        SelectedBatchProduct ??= SelectedCatalogProduct;
        EstimatedLabelCount = SelectedProductsList.Count * Math.Max(1, QuantityPerItem);
        RefreshPreview();
        StatusMessage = $"Added {SelectedCatalogProduct.ProductCode} to batch.";
    }

    [RelayCommand]
    private void AddAllFiltered()
    {
        var added = 0;
        foreach (var product in CatalogProducts)
        {
            if (SelectedProductsList.Any(p =>
                    p.ProductCode.Equals(product.ProductCode, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            SelectedProductsList.Add(product);
            added++;
        }

        EstimatedLabelCount = SelectedProductsList.Count * Math.Max(1, QuantityPerItem);
        SelectedBatchProduct ??= SelectedProductsList.FirstOrDefault();
        RefreshPreview();
        StatusMessage = added == 0 ? "No new products to add." : $"Added {added} product(s) to batch.";
    }

    [RelayCommand]
    private void RemoveSelectedProduct()
    {
        if (SelectedBatchProduct is null)
        {
            return;
        }

        SelectedProductsList.Remove(SelectedBatchProduct);
        SelectedBatchProduct = SelectedProductsList.FirstOrDefault();
        EstimatedLabelCount = SelectedProductsList.Count * Math.Max(1, QuantityPerItem);
        RefreshPreview();
    }

    [RelayCommand]
    private void ClearBatch()
    {
        SelectedProductsList.Clear();
        SelectedBatchProduct = null;
        EstimatedLabelCount = 0;
        PreviewBarcodeImage = null;
        PreviewPriceLine = string.Empty;
        StatusMessage = "Batch cleared.";
    }

    [RelayCommand]
    private async Task PrintBatchAsync()
    {
        if (IsPrinting)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.PrintProductLabels);
            if (SelectedProductsList.Count == 0)
            {
                StatusMessage = "Add at least one product to the batch.";
                return;
            }

            IsPrinting = true;
            var quantity = Math.Max(1, QuantityPerItem);
            var labels = _barcodeService.BuildBatchLabels(SelectedProductsList, quantity);
            if (string.Equals(SelectedTemplateType, LabelTemplateTypes.FiscalQrTag, StringComparison.OrdinalIgnoreCase))
            {
                labels = labels.Select(l => new ProductLabelContent
                {
                    ProductCode = l.ProductCode,
                    ProductName = l.ProductName,
                    UnitPriceNet = l.UnitPriceNet,
                    VatAmount = l.VatAmount,
                    UnitPriceGross = l.UnitPriceGross,
                    VatRatePercent = l.VatRatePercent,
                    Symbology = BarcodeSymbologies.QrCode,
                    BarcodePayload = l.BarcodePayload,
                    QrPayload = _barcodeService.BuildMraVerificationUrl(l.ProductCode),
                    ShowVatInclusive = l.ShowVatInclusive
                }).ToList();
            }

            var batchId = await PersistDraftBatchAsync(labels, quantity).ConfigureAwait(true);
            var result = await _labelTemplateService
                .PrintBatchAsync(labels, SelectedTemplateType)
                .ConfigureAwait(true);

            if (result.Success)
            {
                await _batchRepository.MarkPrintedAsync(batchId).ConfigureAwait(true);
                StatusMessage = $"{result.Message} Batch #{batchId}.";
            }
            else
            {
                await _batchRepository.MarkFailedAsync(batchId, result.Message).ConfigureAwait(true);
                StatusMessage = result.Message;
            }

            await LoadBatchesAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsPrinting = false;
        }
    }

    private async Task InitializeAsync()
    {
        var template = _labelTemplateService.GetTemplate(SelectedTemplateType);
        ActiveTemplateDescription = $"{template.DisplayName} — {template.Description}";
        await RefreshAsync().ConfigureAwait(true);
    }

    private void ApplyCatalogFilter()
    {
        CatalogProducts.Clear();
        var query = ProductSearchQuery?.Trim() ?? string.Empty;
        IEnumerable<LocalInventoryItem> filtered = _allProducts;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = _allProducts.Where(p =>
                p.ProductCode.Contains(query, StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var item in filtered.Take(250))
        {
            CatalogProducts.Add(item);
        }
    }

    private void RefreshPreview()
    {
        var product = SelectedBatchProduct ?? SelectedProductsList.FirstOrDefault() ?? SelectedCatalogProduct;
        if (product is null)
        {
            PreviewBarcodeImage = null;
            PreviewPriceLine = string.Empty;
            return;
        }

        try
        {
            var content = _barcodeService.BuildLabelContent(product);
            PreviewPriceLine =
                $"{content.ProductName} · {LabelPriceFormatter.FormatGrossMwk(content)} · {LabelPriceFormatter.FormatVatLine(content)}";

            if (string.Equals(SelectedTemplateType, LabelTemplateTypes.FiscalQrTag, StringComparison.OrdinalIgnoreCase))
            {
                var url = _barcodeService.BuildMraVerificationUrl(content.ProductCode);
                PreviewBarcodeImage = _barcodeService.GenerateQrBitmap(url);
            }
            else
            {
                PreviewBarcodeImage = _barcodeService.GenerateBarcodeBitmap(
                    content.BarcodePayload,
                    content.Symbology);
            }
        }
        catch (Exception ex)
        {
            PreviewBarcodeImage = null;
            PreviewPriceLine = ex.Message;
        }
    }

    private async Task<long> PersistDraftBatchAsync(IReadOnlyList<ProductLabelContent> labels, int quantity)
    {
        var distinct = labels
            .GroupBy(l => l.ProductCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var batch = new LabelPrintBatch
        {
            TemplateType = SelectedTemplateType,
            QuantityPerItem = quantity,
            ProductCount = distinct.Count,
            LabelCount = labels.Count,
            Status = LabelBatchStatuses.Draft,
            OperatorUsername = _auth.CurrentOperator?.Username,
            Notes = null
        };

        var lines = distinct.Select(l => new LabelPrintBatchLine
        {
            ProductCode = l.ProductCode,
            ProductName = l.ProductName,
            UnitPriceNet = l.UnitPriceNet,
            UnitPriceGross = l.UnitPriceGross,
            Quantity = quantity,
            Symbology = l.Symbology
        }).ToList();

        return await _batchRepository.CreateBatchAsync(batch, lines).ConfigureAwait(false);
    }

    private async Task LoadBatchesAsync()
    {
        RecentBatches.Clear();
        var rows = await _batchRepository.GetRecentAsync(25).ConfigureAwait(true);
        foreach (var row in rows)
        {
            RecentBatches.Add(row);
        }
    }
}
