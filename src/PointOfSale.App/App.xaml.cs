using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
                services.Configure<DatabaseBackupOptions>(
                    context.Configuration.GetSection(DatabaseBackupOptions.SectionName));
                services.Configure<AuthenticationOptions>(
                    context.Configuration.GetSection(AuthenticationOptions.SectionName));
                services.Configure<LoyaltyProgramOptions>(
                    context.Configuration.GetSection(LoyaltyProgramOptions.SectionName));

                services.AddHttpClient(nameof(ApplicationUpdateService));
                services.AddHttpClient(nameof(HeadOfficeSyncService));

                services.AddSingleton<IOfflineInvoiceSyncCompletedHandler, OfflineInvoiceSyncReceiptHandler>();
                services.AddSingleton<GlobalExceptionHandler>();
                services.AddSingleton<IProductionSecretGuard, ProductionSecretGuard>();
                services.AddSingleton<IThermalPrinterHardwareService, ThermalPrinterHardwareService>();
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
                services.AddTransient<IAnalyticsReportExportService, AnalyticsReportExportService>();
                services.AddTransient<ICentralInventoryReplicationService, CentralInventoryReplicationService>();
                services.AddSingleton<IHeadOfficeSyncService, HeadOfficeSyncService>();
                services.AddHostedService<HeadOfficeSyncBackgroundService>();
                services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
                services.AddSingleton<IAuditSecurityLogger, AuditSecurityLogger>();
                services.AddSingleton<IAuthenticationAuthorizationService, AuthenticationAuthorizationService>();
                services.AddTransient<ILoyaltyProgramService, LoyaltyProgramService>();
                services.AddTransient<IPricingRulesEngine, PricingRulesEngine>();

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

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<LoginViewModel>();
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
