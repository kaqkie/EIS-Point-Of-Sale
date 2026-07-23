using PointOfSale.App.Services;
using PointOfSale.Mra.Contracts.Sales;
using Xunit;

namespace PointOfSale.Tests;

public sealed class FiscalConnectivityAndReceiptTests
{
    [Fact]
    public void ProbeTimeout_IsFlooredAt30Seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ConnectionStatusService.ResolveProbeTimeout(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(30), ConnectionStatusService.ResolveProbeTimeout(TimeSpan.FromSeconds(8)));
        Assert.Equal(TimeSpan.FromSeconds(60), ConnectionStatusService.ResolveProbeTimeout(TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void Enricher_BuildsVerificationUrl_WhenMraOmitsIt()
    {
        var enriched = FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
            new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = "ART-1",
                FiscalSignature = "FSIG-LIVE-001"
            },
            "ART-1");

        Assert.Equal("FSIG-LIVE-001", enriched.ResolveFiscalSignature());
        Assert.False(string.IsNullOrWhiteSpace(enriched.VerificationUrl));
        Assert.Contains("invoice=ART-1", enriched.VerificationUrl, StringComparison.Ordinal);
        Assert.Contains("sig=FSIG-LIVE-001", enriched.VerificationUrl, StringComparison.Ordinal);
        Assert.False(FiscalReceiptEnricher.IsOfflinePlaceholder(enriched.ResolveFiscalSignature()));
    }

    [Fact]
    public void Enricher_DoesNotFabricateQr_ForOfflinePlaceholder()
    {
        var enriched = FiscalReceiptEnricher.EnsurePrintableFiscalPayload(
            new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = "ART-2",
                FiscalSignature = FiscalReceiptEnricher.OfflinePendingPlaceholder
            },
            "ART-2");

        Assert.True(FiscalReceiptEnricher.IsOfflinePlaceholder(enriched.ResolveFiscalSignature()));
        Assert.True(string.IsNullOrWhiteSpace(enriched.VerificationUrl));
    }

    [Fact]
    public void HasPrintableFiscalData_True_ForOnlineSignatureWithoutUrl()
    {
        Assert.True(QueueReceiptPrintHelper.HasPrintableFiscalData(
            new SubmitSalesTransactionResponseData
            {
                InvoiceNumber = "ART-3",
                FiscalSignature = "FSIG-ABC"
            }));
    }
}
