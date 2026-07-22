using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PointOfSale.App.Options;

namespace PointOfSale.App.Services;

public sealed class GlobalExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly ITelemetryDiagnosticService? _telemetry;
    private readonly string _criticalLogDirectory;

    public GlobalExceptionHandler(
        ILogger<GlobalExceptionHandler> logger,
        ITelemetryDiagnosticService? telemetry = null)
    {
        _logger = logger;
        _telemetry = telemetry;
        _criticalLogDirectory = Path.Combine(AppContext.BaseDirectory, "Logs", "Critical");
        Directory.CreateDirectory(_criticalLogDirectory);
    }

    public void Register(Application application)
    {
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCritical("DispatcherUnhandledException", e.Exception);
        ShowOperatorMessage(
            "Albert Retail Terminal encountered an unexpected error. The sale screen will remain open. Please contact a supervisor if this continues.");
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogCritical("AppDomainUnhandledException", ex);
            ShowOperatorMessage(
                "A critical error occurred. The application may need to restart after the current operation completes.");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCritical("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void LogCritical(string source, Exception exception)
    {
        _logger.LogCritical(exception, "{Source}: unhandled exception.", source);
        try
        {
            _ = _telemetry?.RecordExceptionAsync(source, exception, DiagnosticSeverities.Critical);
        }
        catch
        {
            // telemetry is advisory during crash paths
        }

        try
        {
            var path = Path.Combine(_criticalLogDirectory, $"critical-{DateTime.UtcNow:yyyyMMdd}.log");
            var entry = new StringBuilder()
                .AppendLine($"----- {DateTime.UtcNow:O} {source} -----")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(path, entry);
        }
        catch
        {
            // last-resort logging must not throw
        }
    }

    private static void ShowOperatorMessage(string message)
    {
        if (Application.Current?.Dispatcher is null)
        {
            return;
        }

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            MessageBox.Show(
                Application.Current.MainWindow,
                message,
                "Albert Retail Terminal",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        });
    }
}
