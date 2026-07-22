using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PointOfSale.App.Deployment;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.App.Services.Compliance;
using PointOfSale.App.ViewModels;
using PointOfSale.App.Views;
using PointOfSale.Infrastructure;
using PointOfSale.Infrastructure.Services;
using Serilog;

namespace PointOfSale.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var environmentName = Environment.GetEnvironmentVariable("ART_ENV")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Sandbox";

        _host = Host.CreateDefaultBuilder()
            .UseSerilog((context, _, configuration) =>
                configuration.ReadFrom.Configuration(context.Configuration))
            .ConfigureAppConfiguration(config =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                config.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddPointOfSaleInfrastructure(context.Configuration);

                services.Configure<TerminalDeploymentOptions>(
                    context.Configuration.GetSection(TerminalDeploymentOptions.SectionName));
                services.Configure<ThermalPrinterOptions>(
                    context.Configuration.GetSection(ThermalPrinterOptions.SectionName));
                services.Configure<ApplicationUpdateOptions>(
                    context.Configuration.GetSection(ApplicationUpdateOptions.SectionName));
                services.Configure<DatabaseBootstrapOptions>(
                    context.Configuration.GetSection(DatabaseBootstrapOptions.SectionName));
                services.Configure<HeadOfficeSyncOptions>(
                    context.Configuration.GetSection(HeadOfficeSyncOptions.SectionName));
                services.Configure<HardwarePeripheralOptions>(
                    context.Configuration.GetSection(HardwarePeripheralOptions.SectionName));
                services.Configure<MultiTerminalSyncOptions>(
                    context.Configuration.GetSection(MultiTerminalSyncOptions.SectionName));
                services.Configure<DatabaseBackupOptions>(
                    context.Configuration.GetSection(DatabaseBackupOptions.SectionName));
                services.Configure<AuthenticationOptions>(
                    context.Configuration.GetSection(AuthenticationOptions.SectionName));
                services.Configure<LoyaltyProgramOptions>(
                    context.Configuration.GetSection(LoyaltyProgramOptions.SectionName));
                services.Configure<LabelPrintingOptions>(
                    context.Configuration.GetSection(LabelPrintingOptions.SectionName));
                services.Configure<InventoryAlertOptions>(
                    context.Configuration.GetSection(InventoryAlertOptions.SectionName));
                services.Configure<GoodsReceiptOptions>(
                    context.Configuration.GetSection(GoodsReceiptOptions.SectionName));
                services.Configure<SystemDiagnosticsOptions>(
                    context.Configuration.GetSection(SystemDiagnosticsOptions.SectionName));
                services.Configure<FinancialClosureOptions>(
                    context.Configuration.GetSection(FinancialClosureOptions.SectionName));
                services.Configure<FiscalArchivalOptions>(
                    context.Configuration.GetSection(FiscalArchivalOptions.SectionName));
                services.Configure<InstallerPackagingOptions>(
                    context.Configuration.GetSection(InstallerPackagingOptions.SectionName));
                services.Configure<EnterprisePerformanceOptions>(
                    context.Configuration.GetSection(EnterprisePerformanceOptions.SectionName));
                services.Configure<EnterpriseMaintenanceOptions>(
                    context.Configuration.GetSection(EnterpriseMaintenanceOptions.SectionName));
                services.Configure<DatabaseMaintenanceOptions>(
                    context.Configuration.GetSection(DatabaseMaintenanceOptions.SectionName));
                services.Configure<MraProductionHandshakeOptions>(
                    context.Configuration.GetSection(MraProductionHandshakeOptions.SectionName));

                services.AddHttpClient(nameof(ApplicationUpdateService));
                services.AddHttpClient(nameof(HeadOfficeSyncService));
                services.AddHttpClient(nameof(TelemetryDiagnosticService));
                services.AddHttpClient(nameof(PerformanceProfilingService));
                services.AddHttpClient(nameof(MraProductionHandshakeService));

                services.AddSingleton<IOfflineInvoiceSyncCompletedHandler, OfflineInvoiceSyncReceiptHandler>();
                services.AddSingleton<ITelemetryDiagnosticService, TelemetryDiagnosticService>();
                services.AddSingleton<GlobalExceptionHandler>();
                services.AddSingleton<IProductionSecretGuard, ProductionSecretGuard>();
                services.AddSingleton<IThermalPrinterHardwareService, ThermalPrinterHardwareService>();
                services.AddSingleton<IHardwarePeripheralService, HardwarePeripheralService>();
                services.AddSingleton<IDatabaseBootstrapService, DatabaseBootstrapService>();
                services.AddSingleton<IApplicationUpdateService, ApplicationUpdateService>();
                services.AddHostedService<ApplicationUpdateBackgroundService>();
                services.AddSingleton<IMraCertificationAuditStore, PointOfSale.App.Services.Compliance.MraCertificationAuditStore>();
                services.AddSingleton<IComplianceCertificationService, ComplianceCertificationService>();
                services.AddSingleton<IComplianceExportService, ComplianceExportService>();
                services.AddTransient<ITaxReconciliationService, TaxReconciliationService>();
                services.AddSingleton<IDatabaseBackupService, DatabaseBackupService>();
                services.AddSingleton<IDatabaseRestorationService, DatabaseRestorationService>();
                services.AddHostedService<DatabaseBackupBackgroundService>();
                services.AddTransient<IShiftManagementService, ShiftManagementService>();
                services.AddTransient<IFinancialClosureService, FinancialClosureService>();
                services.AddTransient<IZReportPrintingService, ZReportPrintingService>();
                services.AddTransient<IFiscalYearRolloverService, FiscalYearRolloverService>();
                services.AddTransient<IArchivalCompressionService, ArchivalCompressionService>();
                services.AddHostedService<ArchivalCompressionBackgroundService>();
                services.AddTransient<ITerminalProvisioningService, TerminalProvisioningService>();
                services.AddSingleton<IIntegrationTestDashboardService, IntegrationTestDashboardService>();
                services.AddTransient<IAnalyticsReportExportService, AnalyticsReportExportService>();
                services.AddTransient<ICentralInventoryReplicationService, CentralInventoryReplicationService>();
                services.AddSingleton<IHeadOfficeSyncService, HeadOfficeSyncService>();
                services.AddHostedService<HeadOfficeSyncBackgroundService>();
                services.AddSingleton<IMultiTerminalSyncBroker, MultiTerminalSyncBroker>();
                services.AddHostedService<MultiTerminalSyncBackgroundService>();
                services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
                services.AddSingleton<IAuditSecurityLogger, AuditSecurityLogger>();
                services.AddSingleton<IAuthenticationAuthorizationService, AuthenticationAuthorizationService>();
                services.AddTransient<ILoyaltyProgramService, LoyaltyProgramService>();
                services.AddTransient<IPricingRulesEngine, PricingRulesEngine>();
                services.AddSingleton<IBarcodeGenerationService, BarcodeGenerationService>();
                services.AddTransient<ILabelTemplateService, LabelTemplateService>();
                services.AddTransient<IInventoryAlertService, InventoryAlertService>();
                services.AddTransient<IPurchaseOrderGenerationService, PurchaseOrderGenerationService>();
                services.AddTransient<IGoodsReceiptService, GoodsReceiptService>();
                services.AddTransient<ISupplierInvoiceReconciliationService, SupplierInvoiceReconciliationService>();
                services.AddHostedService<InventoryAlertBackgroundService>();
                services.AddHostedService<HealthCheckWorker>();
                services.AddSingleton<IPerformanceProfilingService, PerformanceProfilingService>();
                services.AddTransient<IEnterpriseMaintenanceService, EnterpriseMaintenanceService>();
                services.AddHostedService<PerformanceProfilingBackgroundService>();
                services.AddSingleton<IDatabaseMaintenanceService, DatabaseMaintenanceService>();
                services.AddHostedService<DatabaseMaintenanceBackgroundService>();
                services.AddSingleton<IMraProductionHandshakeService, MraProductionHandshakeService>();
                services.AddHostedService<MraProductionHandshakeMonitor>();

                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IConnectionStatusService, ConnectionStatusService>();
                services.AddSingleton<IPosConfigurationService, PosConfigurationService>();
                services.AddSingleton<IReceiptPrintingService, ReceiptPrintingService>();

                services.AddTransient<CheckoutView>();
                services.AddTransient<InventoryView>();
                services.AddTransient<QueueSyncStatusView>();
                services.AddTransient<ComplianceExportView>();
                services.AddTransient<AdminAnalyticsView>();
                services.AddTransient<HeadOfficeSyncView>();
                services.AddTransient<BackupRecoveryView>();
                services.AddTransient<LoginView>();
                services.AddTransient<UserManagementView>();
                services.AddTransient<CustomerLoyaltyView>();
                services.AddTransient<DiscountManagementView>();
                services.AddTransient<BarcodePrintingView>();
                services.AddTransient<InventoryAlertsView>();
                services.AddTransient<PurchaseOrderManagementView>();
                services.AddTransient<GoodsReceiptView>();
                services.AddTransient<SupplierInvoiceReconciliationView>();
                services.AddTransient<SystemDiagnosticsView>();
                services.AddTransient<EndofDaySummaryView>();
                services.AddTransient<FiscalRolloverView>();
                services.AddTransient<TerminalProvisioningView>();
                services.AddTransient<TestRunnerDashboardView>();
                services.AddTransient<EnterpriseMaintenanceView>();

                services.AddTransient<DatabaseMaintenanceView>();
                services.AddTransient<ComplianceAuditView>();
                services.AddTransient<HardwareManagementView>();
                services.AddTransient<AuthenticationView>();
                services.AddTransient<CashierDashboardView>();
                services.AddTransient<AdminDashboardView>();

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<LoginViewModel>();
                services.AddSingleton<AuthenticationViewModel>();
                services.AddTransient<CheckoutViewModel>();
                services.AddTransient<InventoryViewModel>();
                services.AddTransient<QueueSyncStatusViewModel>();
                services.AddTransient<ComplianceExportViewModel>();
                services.AddTransient<AdminAnalyticsViewModel>();
                services.AddTransient<HeadOfficeSyncViewModel>();
                services.AddTransient<BackupRecoveryViewModel>();
                services.AddTransient<UserManagementViewModel>();
                services.AddTransient<CustomerLoyaltyViewModel>();
                services.AddTransient<DiscountManagementViewModel>();
                services.AddTransient<BarcodePrintingViewModel>();
                services.AddTransient<InventoryAlertsViewModel>();
                services.AddTransient<PurchaseOrderManagementViewModel>();
                services.AddTransient<GoodsReceiptViewModel>();
                services.AddTransient<SupplierInvoiceReconciliationViewModel>();
                services.AddTransient<SystemDiagnosticsViewModel>();
                services.AddTransient<EndofDaySummaryViewModel>();
                services.AddTransient<FiscalRolloverViewModel>();
                services.AddTransient<TerminalProvisioningViewModel>();
                services.AddTransient<TestRunnerDashboardViewModel>();
                services.AddTransient<EnterpriseMaintenanceViewModel>();
                services.AddTransient<DatabaseMaintenanceViewModel>();
                services.AddTransient<ComplianceAuditViewModel>();
                services.AddTransient<HardwareManagementViewModel>();
                services.AddTransient<CashierDashboardViewModel>();
                services.AddTransient<AdminDashboardViewModel>();

                services.AddSingleton<MainWindow>();
            })
            .Build();

        _host.Services.GetRequiredService<GlobalExceptionHandler>().Register(this);

        try
        {
            var updater = _host.Services.GetRequiredService<IApplicationUpdateService>();
            if (await updater.TryApplyStagedUpdateOnStartupAsync().ConfigureAwait(true))
            {
                return;
            }

            await _host.Services.GetRequiredService<IDatabaseBootstrapService>()
                .EnsureDatabaseReadyAsync()
                .ConfigureAwait(true);

            InstallerConfiguration.EnsureStandardDirectories();

            await _host.Services.GetRequiredService<IAuthenticationAuthorizationService>()
                .EnsureSeededAsync()
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Albert Retail Terminal — Startup",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        await _host.StartAsync().ConfigureAwait(true);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Log.CloseAndFlush();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    public static IServiceProvider Services =>
        ((App)Current)._host?.Services
        ?? throw new InvalidOperationException("Application host is not initialized.");
}
