using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PointOfSale.App.Services;
using PointOfSale.App.ViewModels;
using PointOfSale.App.Views;
using PointOfSale.Infrastructure;
using PointOfSale.Infrastructure.Services;

namespace PointOfSale.App;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(config =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddPointOfSaleInfrastructure(context.Configuration);
                services.AddSingleton<IOfflineInvoiceSyncCompletedHandler, OfflineInvoiceSyncReceiptHandler>();

                services.AddSingleton<INavigationService, NavigationService>();
                services.AddSingleton<IConnectionStatusService, ConnectionStatusService>();
                services.AddSingleton<IPosConfigurationService, PosConfigurationService>();
                services.AddSingleton<IReceiptPrintingService, ReceiptPrintingService>();

                services.AddTransient<CheckoutView>();
                services.AddTransient<InventoryView>();
                services.AddTransient<QueueSyncStatusView>();

                services.AddSingleton<MainViewModel>();
                services.AddTransient<CheckoutViewModel>();
                services.AddTransient<InventoryViewModel>();
                services.AddTransient<QueueSyncStatusViewModel>();

                services.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync().ConfigureAwait(true);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            _host.Dispose();
        }

        base.OnExit(e);
    }

    public static IServiceProvider Services =>
        ((App)Current)._host?.Services
        ?? throw new InvalidOperationException("Application host is not initialized.");
}
