// Submit queue invoices through MraFiscalPayloadNormalizer (Item-mode gross unitPrice).
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PointOfSale.Infrastructure;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Http;
using PointOfSale.Mra.Serialization;

var config = new ConfigurationBuilder()
    .SetBasePath(@"c:\Users\Albert Zee\Documents\Projects\Point Of Sale\src\PointOfSale.App")
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Sandbox.json", optional: true)
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<IConfiguration>(config);
services.AddPointOfSaleInfrastructure(config);

await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var auth = scope.ServiceProvider.GetRequiredService<IMraTerminalAuthProvider>();
var api = scope.ServiceProvider.GetRequiredService<MraApiClient>();
var queue = scope.ServiceProvider.GetRequiredService<IOfflineInvoiceQueueRepository>();
var ctx = await auth.GetSignedContextAsync();
const string site = "BL7a9fe868-d512-4198-8b08-497e8f0fc10a";

foreach (var id in new[] { 3009, 3010 })
{
    var item = await queue.GetByIdAsync(id);
    if (item is null)
    {
        Console.WriteLine($"{id}: missing");
        continue;
    }

    var src = JsonSerializer.Deserialize<SubmitSalesTransactionRequest>(item.PayloadJson, MraJson.SerializerOptions)!;
    var normalized = MraFiscalPayloadNormalizer.Normalize(
        src,
        new MraFiscalIdentityOverlay(
            SellerTin: "20122074",
            SiteId: site,
            GlobalConfigVersion: 1,
            TaxpayerConfigVersion: 1,
            TerminalConfigVersion: 1,
            StandardTaxRateId: "A"));

    var header = normalized.InvoiceHeader;
    normalized = normalized with
    {
        InvoiceHeader = new InvoiceHeaderDto
        {
            InvoiceNumber = header.InvoiceNumber,
            InvoiceDateTime = OfflineSalesQueueService.NormalizeInvoiceDateTime(DateTime.UtcNow),
            SellerTin = header.SellerTin,
            BuyerTin = header.BuyerTin,
            BuyerName = header.BuyerName,
            BuyerAuthorizationCode = header.BuyerAuthorizationCode,
            SiteId = header.SiteId,
            GlobalConfigVersion = header.GlobalConfigVersion,
            TaxpayerConfigVersion = header.TaxpayerConfigVersion,
            TerminalConfigVersion = header.TerminalConfigVersion,
            IsExport = header.IsExport,
            IsReliefSupply = header.IsReliefSupply,
            Vat5CertificateDetails = header.Vat5CertificateDetails,
            PaymentMethod = header.PaymentMethod
        },
        InvoiceSummary = normalized.InvoiceSummary with { OfflineSignature = null }
    };

    Console.WriteLine(
        $"{id} unitPrices=[{string.Join(",", normalized.InvoiceLineItems.Select(l => l.UnitPrice))}] " +
        $"totals=[{string.Join(",", normalized.InvoiceLineItems.Select(l => l.Total))}] " +
        $"vats=[{string.Join(",", normalized.InvoiceLineItems.Select(l => l.TotalVat))}] " +
        $"invoiceTotal={normalized.InvoiceSummary.InvoiceTotal}");

    try
    {
        var response = await api.PostAsync<SubmitSalesTransactionRequest, SubmitSalesTransactionResponseData>(
            "sales/submit-sales-transaction", normalized, ctx);
        Console.WriteLine($"{id}: success={response.IsSuccess} status={response.StatusCode} remark={response.Remark}");
        if (response.IsSuccess)
        {
            await queue.MarkSyncedAsync(id, JsonSerializer.Serialize(response.Data, MraJson.SerializerOptions));
            Console.WriteLine($"{id}: marked SYNCED");
        }
    }
    catch (MraApiException ex)
    {
        Console.WriteLine($"{id}: HTTP {ex.HttpStatusCode} {ex.ResponseBody}");
    }
}

return 0;
