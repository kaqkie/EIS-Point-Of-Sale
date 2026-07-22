using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class SystemDiagnosticsViewModel : ObservableObject
{
    private readonly ITelemetryDiagnosticService _telemetry;
    private readonly IAuthenticationAuthorizationService _auth;

    public SystemDiagnosticsViewModel(
        ITelemetryDiagnosticService telemetry,
        IAuthenticationAuthorizationService auth)
    {
        _telemetry = telemetry;
        _auth = auth;
        RecentErrorLogsList = new ObservableCollection<DiagnosticTelemetryEvent>();
        CategoryFilters = new ObservableCollection<string>
        {
            "All",
            DiagnosticEventCategories.Exception,
            DiagnosticEventCategories.DatabaseLatency,
            DiagnosticEventCategories.WorkerHeartbeat,
            DiagnosticEventCategories.MraConnectivity,
            DiagnosticEventCategories.HealthCheck,
            DiagnosticEventCategories.Disk,
            DiagnosticEventCategories.Printer
        };
        SeverityFilters = new ObservableCollection<string>
        {
            "All",
            DiagnosticSeverities.Information,
            DiagnosticSeverities.Warning,
            DiagnosticSeverities.Error,
            DiagnosticSeverities.Critical
        };
        SelectedCategoryFilter = "All";
        SelectedSeverityFilter = "All";
        _telemetry.HealthChanged += OnHealthChanged;
        ApplySnapshot(_telemetry.LatestSnapshot);
        _ = RefreshAsync();
    }

    public ObservableCollection<DiagnosticTelemetryEvent> RecentErrorLogsList { get; }
    public ObservableCollection<string> CategoryFilters { get; }
    public ObservableCollection<string> SeverityFilters { get; }

    [ObservableProperty]
    private bool _isDatabaseHealthy = true;

    [ObservableProperty]
    private string _mraApiStatus = "Unknown";

    [ObservableProperty]
    private string _availableDiskSpace = "—";

    [ObservableProperty]
    private string _printerStatus = "—";

    [ObservableProperty]
    private int _databaseLatencyMs;

    [ObservableProperty]
    private string _healthSummary = "Run diagnostics to refresh subsystem status.";

    [ObservableProperty]
    private bool _overallHealthy = true;

    [ObservableProperty]
    private string _selectedCategoryFilter = "All";

    [ObservableProperty]
    private string _selectedSeverityFilter = "All";

    [ObservableProperty]
    private string _logSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isRunningDiagnostics;

    [ObservableProperty]
    private string _statusMessage = "System diagnostics and telemetry.";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ViewSystemDiagnostics);
            await LoadLogsAsync().ConfigureAwait(true);
            ApplySnapshot(_telemetry.LatestSnapshot);
            StatusMessage = $"Loaded {RecentErrorLogsList.Count} diagnostic event(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task FilterLogsAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ViewSystemDiagnostics);
            await LoadLogsAsync().ConfigureAwait(true);
            StatusMessage = $"Filter applied — {RecentErrorLogsList.Count} event(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RunDiagnosticsAsync()
    {
        if (IsRunningDiagnostics)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ViewSystemDiagnostics);
            IsRunningDiagnostics = true;
            var snapshot = await _telemetry.RunDiagnosticsAsync().ConfigureAwait(true);
            ApplySnapshot(snapshot);
            await LoadLogsAsync().ConfigureAwait(true);
            StatusMessage = snapshot.Summary;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsRunningDiagnostics = false;
        }
    }

    private async Task LoadLogsAsync()
    {
        RecentErrorLogsList.Clear();
        var rows = await _telemetry.GetRecentLogsAsync(
                SelectedCategoryFilter,
                SelectedSeverityFilter,
                LogSearchQuery)
            .ConfigureAwait(true);
        foreach (var row in rows)
        {
            RecentErrorLogsList.Add(row);
        }
    }

    private void OnHealthChanged(object? sender, SystemHealthSnapshot snapshot)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            ApplySnapshot(snapshot);
            return;
        }

        if (dispatcher.CheckAccess())
        {
            ApplySnapshot(snapshot);
        }
        else
        {
            dispatcher.Invoke(() => ApplySnapshot(snapshot));
        }
    }

    private void ApplySnapshot(SystemHealthSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        IsDatabaseHealthy = snapshot.IsDatabaseHealthy;
        DatabaseLatencyMs = snapshot.DatabaseLatencyMs;
        MraApiStatus = snapshot.MraApiStatus;
        AvailableDiskSpace = FormatBytes(snapshot.AvailableDiskSpaceBytes);
        PrinterStatus = snapshot.PrinterStatus;
        HealthSummary = snapshot.Summary;
        OverallHealthy = snapshot.OverallHealthy;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "—";
        }

        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
