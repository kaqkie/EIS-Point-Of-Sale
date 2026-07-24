using Microsoft.Extensions.Options;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Pricing;
using Xunit;

namespace PointOfSale.Tests;

public sealed class BarcodeLabelTests
{
    private static IBarcodeGenerationService CreateBarcodeService() =>
        new BarcodeGenerationService(Options.Create(new LabelPrintingOptions
        {
            ShowVatInclusivePrice = true,
            MraVerificationBaseUrl = "https://dev-eis-portal.mra.mw/verify",
            PreviewBarcodeHeightPx = 64,
            PreviewBarcodeWidthPx = 240
        }));

    [Fact]
    public void Ean13_CheckDigit_IsComputedCorrectly()
    {
        // Classic EAN-13 example: 5901234123457
        Assert.Equal(7, BarcodeGenerationService.ComputeEan13CheckDigit("590123412345"));
    }

    [Fact]
    public void ResolveSymbology_PrefersEan13ForDigitSkus()
    {
        var service = CreateBarcodeService();
        Assert.Equal(BarcodeSymbologies.Ean13, service.ResolveSymbology("5901234123457"));
        Assert.Equal(BarcodeSymbologies.Code128, service.ResolveSymbology("SKU-ABC-001"));
    }

    [Fact]
    public void BuildLabelContent_Applies17_5VatToGrossPrice()
    {
        var service = CreateBarcodeService();
        var product = new LocalInventoryItem
        {
            ProductId = "1",
            ProductCode = "SKU-100",
            Name = "Cooking Oil 1L",
            UnitPrice = 1000m,
            StockQuantity = 10,
            TaxRateId = "T"
        };

        var label = service.BuildLabelContent(product);
        Assert.Equal(1000m, label.UnitPriceNet);
        Assert.Equal(175m, label.VatAmount);
        Assert.Equal(1175m, label.UnitPriceGross);
        Assert.Equal(PosTaxCalculator.MalawiStandardVatRatePercent, label.VatRatePercent);
        Assert.Equal(BarcodeSymbologies.Code128, label.Symbology);
    }

    [Fact]
    public void BuildBatchLabels_RepeatsByQuantity()
    {
        var service = CreateBarcodeService();
        var products = new[]
        {
            new LocalInventoryItem
            {
                ProductId = "1",
                ProductCode = "A",
                Name = "A",
                UnitPrice = 100m
            },
            new LocalInventoryItem
            {
                ProductId = "2",
                ProductCode = "B",
                Name = "B",
                UnitPrice = 200m
            }
        };

        var labels = service.BuildBatchLabels(products, quantityPerItem: 3);
        Assert.Equal(6, labels.Count);
        Assert.Equal(3, labels.Count(l => l.ProductCode == "A"));
    }

    [Fact]
    public void BuildMraVerificationUrl_EmbedsInvoiceId()
    {
        var service = CreateBarcodeService();
        var url = service.BuildMraVerificationUrl("ART-20260722-001", "SIG123");
        Assert.Contains("invoice=ART-20260722-001", url);
        Assert.Contains("sig=SIG123", url);
        Assert.StartsWith("https://dev-eis-portal.mra.mw/verify", url);
    }

    [Fact]
    public void EscPosLabelEncoder_EmitsCode128AndText()
    {
        var label = new ProductLabelContent
        {
            ProductCode = "SKU-100",
            ProductName = "Cooking Oil",
            UnitPriceNet = 1000m,
            VatAmount = 175m,
            UnitPriceGross = 1175m,
            VatRatePercent = 17.5m,
            Symbology = BarcodeSymbologies.Code128,
            BarcodePayload = "SKU-100",
            ShowVatInclusive = true
        };

        var template = new LabelTemplateDefinition
        {
            TemplateType = LabelTemplateTypes.ShelfEdge50x30,
            DisplayName = "Shelf",
            WidthMm = 50,
            HeightMm = 30,
            CharactersPerLine = 32,
            IncludeBarcode = true
        };

        var bytes = EscPosLabelEncoder.Encode(label, template);
        Assert.True(bytes.Length > 40);
        Assert.Contains((byte)0x1D, bytes); // GS
        Assert.Contains((byte)0x6B, bytes); // barcode command
    }

    [Fact]
    public void LabelTemplates_IncludeStandardShelfSizes()
    {
        Assert.Contains(LabelTemplateTypes.ShelfEdge50x30, LabelTemplateTypes.All);
        Assert.Contains(LabelTemplateTypes.ShelfEdge40x25, LabelTemplateTypes.All);
        Assert.Contains(LabelTemplateTypes.StickyBarcode, LabelTemplateTypes.All);
        Assert.Contains(LabelTemplateTypes.FiscalQrTag, LabelTemplateTypes.All);
    }
}
