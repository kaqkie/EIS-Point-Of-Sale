using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class DatabaseMaintenanceViewModel : ObservableObject, IDisposable
{
    private readonly IDatabaseMaintenanceService _maintenance;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    public DatabaseMaintenanceViewModel(
        IDatabaseMaintenanceService maintenance,
        IAuthenticationAuthorizationService auth)
    {
        _maintenance = maintenance;
        _auth = auth;
        LogEntries = new ObservableCollection<DatabaseMaintenanceLogRowViewModel>();

        _maintenance.DashboardUpdated += (_, _) => _ = RefreshAsync();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        _refreshTimer.Start();

        _ = InitializeAsync();
    }

    public ObservableCollection<DatabaseMaintenanceLogRowViewModel> LogEntries { get; }

    [ObservableProperty]
    private long _databaseSizeMb;

    [ObservableProperty]
    private int _fragmentedIndexesCount;

    [ObservableProperty]
    private string _lastOptimizationTimestamp = "Never";

    [ObservableProperty]
    private bool _isMaintenanceRunning;

    [ObservableProperty]
    private string _statusMessage = "Loading database maintenance status…";

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var dashboard = await _maintenance.GetDashboardAsync().ConfigureAwait(true);
            DatabaseSizeMb = dashboard.DatabaseSizeMb;
            FragmentedIndexesCount = dashboard.FragmentedIndexesCount;
            LastOptimizationTimestamp = dashboard.LastOptimizationTimestampUtc is null
                ? "Never"
                : dashboard.LastOptimizationTimestampUtc.Value.ToLocalTime().ToString("g");
            IsMaintenanceRunning = dashboard.IsMaintenanceRunning;
            StatusMessage = dashboard.StatusSummary;

            var logs = await _maintenance.GetRecentLogsAsync().ConfigureAwait(true);
            LogEntries.Clear();
            foreach (var row in logs)
            {
                LogEntries.Add(DatabaseMaintenanceLogRowViewModel.From(row));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task RunFullOptimizationAsync() =>
        await RunAsync(Core.Entities.DatabaseMaintenanceOperations.FullOptimization).ConfigureAwait(true);

    [RelayCommand]
    private async Task RebuildIndexesAsync() =>
        await RunAsync(Core.Entities.DatabaseMaintenanceOperations.RebuildIndexes).ConfigureAwait(true);

    [RelayCommand]
    private async Task UpdateStatisticsAsync() =>
        await RunAsync(Core.Entities.DatabaseMaintenanceOperations.UpdateStatistics).ConfigureAwait(true);

    [RelayCommand]
    private async Task PurgeTelemetryAsync() =>
        await RunAsync(Core.Entities.DatabaseMaintenanceOperations.PurgeTelemetry).ConfigureAwait(true);

    private async Task InitializeAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManageDatabaseMaintenance);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task RunAsync(string operation)
    {
        if (IsMaintenanceRunning)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ManageDatabaseMaintenance);
            IsMaintenanceRunning = true;
            StatusMessage = $"Running {operation}…";
            var result = await _maintenance.RunMaintenanceAsync(operation).ConfigureAwait(true);
            StatusMessage = result.Success ? result.Detail ?? "Completed." : result.Detail ?? "Failed.";
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsMaintenanceRunning = _maintenance.IsMaintenanceRunning;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
    }
}

public sealed class DatabaseMaintenanceLogRowViewModel
{
    public long LogId { get; init; }
    public string ExecutedAtLocal { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Detail { get; init; } = string.Empty;
    public int DurationMs { get; init; }

    public static DatabaseMaintenanceLogRowViewModel From(Core.Entities.DatabaseMaintenanceLogEntry entry) =>
        new()
        {
            LogId = entry.LogId,
            ExecutedAtLocal = entry.ExecutedAtUtc.ToLocalTime().ToString("g"),
            Operation = entry.Operation,
            Success = entry.Success,
            Detail = entry.Detail ?? string.Empty,
            DurationMs = entry.DurationMs
        };
}
