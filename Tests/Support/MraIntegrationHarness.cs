using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Options;
using PointOfSale.Tests.Mocks;
using PointOfSale.Tests.Support;

namespace PointOfSale.Tests.Support;

public sealed class MraIntegrationHarness : IDisposable
{
    public MraIntegrationHarness(MockMraServer mockServer, TimeSpan? httpTimeout = null)
    {
        MockServer = mockServer;
        QueueRepository = new InMemoryOfflineInvoiceQueueRepository();
        InventoryRepository = new FakeLocalInventoryRepository();
        ConfigurationRepository = new FakeConfigurationRepository();
        AuthProvider = new TestMraTerminalAuthProvider();

        var httpClient = new HttpClient(mockServer.HttpHandler) { BaseAddress = new Uri(mockServer.BaseUrl) };
        var mraOptions = Options.Create(new MraApiOptions
        {
            BaseUrl = mockServer.BaseUrl,
            HttpTimeout = httpTimeout ?? TimeSpan.FromSeconds(30)
        });

        ApiClient = new MraApiClient(httpClient, mraOptions, NullLogger<MraApiClient>.Instance);
        StockService = new StockManagementService(
            ApiClient,
            AuthProvider,
            InventoryRepository,
            ConfigurationRepository,
            NullLogger<StockManagementService>.Instance,
            Options.Create(new PosOperationsOptions { InventoryUploadBatchSize = 50 }));

        SalesService = new SalesTransactionService(
            ApiClient,
            AuthProvider,
            InventoryRepository,
            StockService,
            NullLogger<SalesTransactionService>.Instance);

        OfflineQueueService = new OfflineSalesQueueService(
            QueueRepository,
            SalesService,
            Options.Create(new OfflineSyncOptions { MaxRetryAttempts = 3, BaseBackoffSeconds = 1, MaxBackoffSeconds = 5 }),
            NullLogger<OfflineSalesQueueService>.Instance);
    }

    public MockMraServer MockServer { get; }
    public InMemoryOfflineInvoiceQueueRepository QueueRepository { get; }
    public FakeLocalInventoryRepository InventoryRepository { get; }
    public FakeConfigurationRepository ConfigurationRepository { get; }
    public TestMraTerminalAuthProvider AuthProvider { get; }
    public MraApiClient ApiClient { get; }
    public StockManagementService StockService { get; }
    public SalesTransactionService SalesService { get; }
    public OfflineSalesQueueService OfflineQueueService { get; }

    public void Dispose()
    {
    }
}
