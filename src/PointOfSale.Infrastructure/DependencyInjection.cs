using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PointOfSale.Core.Compliance;
using PointOfSale.Infrastructure.Data;
using PointOfSale.Infrastructure.Repositories;
using PointOfSale.Infrastructure.Security;
using PointOfSale.Infrastructure.Options;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Infrastructure.Workers;

using PointOfSale.Mra.Options;

namespace PointOfSale.Infrastructure;

public static class DependencyInjection
{
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddPointOfSaleInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<MraApiOptions>(configuration.GetSection(MraApiOptions.SectionName));
        services.PostConfigure<MraApiOptions>(options =>
        {
            var timeoutSeconds = configuration.GetValue<int?>("MraEis:HttpTimeoutSeconds")
                ?? (options.HttpTimeoutSeconds > 0 ? options.HttpTimeoutSeconds : null);
            if (timeoutSeconds is > 0)
            {
                // Floor at 30s so slow EIS handshakes do not false-trigger offline queueing.
                options.HttpTimeout = TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds.Value));
                options.HttpTimeoutSeconds = (int)options.HttpTimeout.TotalSeconds;
            }
            else if (options.HttpTimeout < TimeSpan.FromSeconds(30))
            {
                options.HttpTimeout = TimeSpan.FromSeconds(30);
                options.HttpTimeoutSeconds = 30;
            }

            if (string.IsNullOrWhiteSpace(options.BaseUrl))
            {
                options.BaseUrl = options.ResolveBaseUrl();
            }
        });

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

        services.AddHttpClient<MraApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(30),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12
                        | System.Security.Authentication.SslProtocols.Tls13
                }
            })
            .ConfigureHttpClient((sp, client) =>
            {
                var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MraApiOptions>>().Value;
                var timeout = opts.HttpTimeout < TimeSpan.FromSeconds(30)
                    ? TimeSpan.FromSeconds(30)
                    : opts.HttpTimeout;
                client.Timeout = timeout;
            });
        services.AddScoped<IMraTerminalAuthProvider, MraTerminalAuthProvider>();
        services.AddScoped<TerminalOnboardingService>();
        services.AddScoped<StockManagementService>();
        services.AddScoped<SalesTransactionService>();
        services.AddScoped<OfflineSalesQueueService>();
        services.AddHostedService<OfflineInvoiceFifoSyncBackgroundService>();

        return services;
    }
}
