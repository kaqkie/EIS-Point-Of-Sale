// Force-sync queue items 3009/3010 after terminal activation / fiscal repairs.
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PointOfSale.Infrastructure;
using PointOfSale.Infrastructure.Services;

var config = new ConfigurationBuilder()
    .SetBasePath(@"c:\Users\Albert Zee\Documents\Projects\Point Of Sale\src\PointOfSale.App")
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Sandbox.json", optional: true)
    .Build();

var services = new ServiceCollection();
services.AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
services.AddSingleton<IConfiguration>(config);
services.AddPointOfSaleInfrastructure(config);

await using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var queue = scope.ServiceProvider.GetRequiredService<OfflineSalesQueueService>();

foreach (var id in new[] { 3009, 3010 })
{
    Console.WriteLine($"ForceSync {id}...");
    var result = await queue.ForceSyncQueueItemAsync(id);
    if (result is null)
    {
        Console.WriteLine($"  {id}: not found");
        continue;
    }

    Console.WriteLine(
        $"  {id}: quarantined={result.IsQuarantined} submitted={result.SubmittedOnline} invoice={result.InvoiceNumber} remark={result.Remark}");
}

return 0;
