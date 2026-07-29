using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PointOfSale.Core.Compliance;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Http;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Security;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Infrastructure.Workers;
using PointOfSale.Mra.Options;
using PointOfSale.Mra.Services;

namespace PointOfSale.Infrastructure;

public static class DependencyInjection
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddPointOfSaleInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        MraApiOptionsConfiguration.Apply(services, configuration);

        services.Configure<OfflineSyncOptions>(configuration.GetSection(OfflineSyncOptions.SectionName));
        services.Configure<PosOperationsOptions>(configuration.GetSection(PosOperationsOptions.SectionName));
        services.Configure<AuditLoggingOptions>(configuration.GetSection(AuditLoggingOptions.SectionName));

        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
#pragma warning disable CA1416 // Albert Retail Terminal is Windows-only (WPF + DPAPI)
        services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
#pragma warning restore CA1416
        services.AddSingleton<IAuditLoggingService, AuditLoggingService>();
        services.AddSingleton<MraRuntimeEnvironmentState>();
        services.AddSingleton<IComplianceAuditLogger, ComplianceAuditLoggingService>();

        services.AddScoped<ITerminalRepository, TerminalRepository>();
        services.AddScoped<IConfigurationRepository, ConfigurationRepository>();
        services.AddScoped<IOfflineInvoiceQueueRepository, OfflineInvoiceQueueRepository>();
        services.AddScoped<ILocalInventoryRepository, LocalInventoryRepository>();
        services.AddScoped<ICashierShiftRepository, CashierShiftRepository>();
        services.AddScoped<IHeadOfficeSyncOutboxRepository, HeadOfficeSyncOutboxRepository>();
        services.AddScoped<IOperatorRepository, OperatorRepository>();
        services.AddScoped<ILoyaltyMemberRepository, LoyaltyMemberRepository>();
        services.AddScoped<IPricingRuleRepository, PricingRuleRepository>();
        services.AddScoped<ILabelPrintBatchRepository, LabelPrintBatchRepository>();
        services.AddScoped<IInventorySupplierRepository, InventorySupplierRepository>();
        services.AddScoped<IInventoryStockAlertRepository, InventoryStockAlertRepository>();
        services.AddScoped<IPurchaseOrderRepository, PurchaseOrderRepository>();
        services.AddScoped<IGoodsReceiptRepository, GoodsReceiptRepository>();
        services.AddScoped<ISupplierInvoiceReconciliationRepository, SupplierInvoiceReconciliationRepository>();
        services.AddSingleton<IDiagnosticTelemetryRepository, DiagnosticTelemetryRepository>();
        services.AddSingleton<IFinancialClosureRepository, FinancialClosureRepository>();
        services.AddScoped<IFiscalYearArchiveRepository, FiscalYearArchiveRepository>();
        services.AddScoped<IMultiTerminalSyncRepository, MultiTerminalSyncRepository>();

        services.AddTransient<MraEisMessageHashHandler>();
        services.AddHttpClient(MraHttpClientFactory.ClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var opts = sp.GetRequiredService<IOptions<MraApiOptions>>().Value;
                return MraHttpClientFactory.CreateHandler(opts);
            })
            .AddHttpMessageHandler<MraEisMessageHashHandler>()
            .ConfigureHttpClient((sp, client) =>
            {
                var opts = sp.GetRequiredService<IOptions<MraApiOptions>>().Value;
                MraHttpClientFactory.ConfigureClient(client, opts);
            });

        // Explicit factory avoids ambiguous ctor activation (HttpClient is creatable via Activator).
        services.AddTransient(sp => new MraApiClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<MraApiOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<MraApiClient>>(),
            sp.GetService<IAuditLoggingService>(),
            sp.GetService<MraRuntimeEnvironmentState>()));
        services.AddSingleton<IMraEisResponseEvaluator, MraEisResponseEvaluator>();
        services.AddSingleton<ILastSubmittedOfflineTransactionResponseService, LastSubmittedOfflineTransactionResponseService>();
        services.AddSingleton<ILastSubmittedOnlineTransactionResponseService, LastSubmittedOnlineTransactionResponseService>();
        services.AddSingleton<ITerminalSiteProductsResponseService, TerminalSiteProductsResponseService>();
        services.AddScoped<ITerminalSiteProductsCatalogSyncService, TerminalSiteProductsCatalogSyncService>();
        services.AddScoped<IMraTerminalAuthProvider, MraTerminalAuthProvider>();
        services.AddScoped<TerminalOnboardingService>();
        services.AddScoped<StockManagementService>();
        services.AddScoped<SalesTransactionService>();
        services.AddSingleton<IMraInvoiceSequenceService, MraInvoiceSequenceService>();
        services.AddScoped<OfflineSalesQueueService>();
        services.AddHostedService<OfflineInvoiceFifoSyncBackgroundService>();

        return services;
    }
}
