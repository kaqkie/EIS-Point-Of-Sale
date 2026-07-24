using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using PointOfSale.App.Options;

namespace PointOfSale.App.Services;

public sealed class GlobalExceptionHandler
{
    private static readonly TimeSpan OperatorMessageCooldown = TimeSpan.FromSeconds(30);

    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly ITelemetryDiagnosticService? _telemetry;
    private readonly string _criticalLogDirectory;
    private readonly object _messageGate = new();
    private DateTime _lastOperatorMessageUtc = DateTime.MinValue;

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
            IsRecoverableUiFault(e.Exception)
                ? "A display refresh failed and was recovered. You can continue working; contact a supervisor if this keeps happening."
                : "Albert Retail Terminal encountered an unexpected error. The sale screen will remain open. Please contact a supervisor if this continues.",
            MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is not Exception ex)
        {
            return;
        }

        LogCritical("AppDomainUnhandledException", ex);

        // Prefer continuing the terminal workflow: log stack traces and warn the operator
        // without forcing a restart for recoverable UI / binding faults.
        if (IsRecoverableUiFault(ex) || !e.IsTerminating)
        {
            ShowOperatorMessage(
                "A background error was logged and contained. You can continue; pending invoices stay in the sync queue until they succeed or are quarantined.",
                MessageBoxImage.Warning);
            return;
        }

        ShowOperatorMessage(
            "A critical error was logged. Finish the current sale if possible, then restart Albert Retail Terminal.",
            MessageBoxImage.Error);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCritical("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private void LogCritical(string source, Exception exception)
    {
        Debug.WriteLine($"[{source}] {exception}");
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

    private void ShowOperatorMessage(string message, MessageBoxImage icon)
    {
        lock (_messageGate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastOperatorMessageUtc < OperatorMessageCooldown)
            {
                return;
            }

            _lastOperatorMessageUtc = now;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.InvokeAsync(() =>
        {
            MessageBox.Show(
                Application.Current?.MainWindow,
                message,
                "Albert Retail Terminal",
                MessageBoxButton.OK,
                icon);
        });
    }

    private static bool IsRecoverableUiFault(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NotSupportedException &&
                current.Message.Contains("CollectionView", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (current is InvalidOperationException &&
                (current.Message.Contains("Dispatcher", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("calling thread", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("constructors are ambiguous", StringComparison.OrdinalIgnoreCase) ||
                 current.Message.Contains("CollectionView", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (current is ObjectDisposedException)
            {
                return true;
            }
        }

        return false;
    }
}
