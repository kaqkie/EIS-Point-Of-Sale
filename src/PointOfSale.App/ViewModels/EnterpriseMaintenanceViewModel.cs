using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class EnterpriseMaintenanceViewModel : ObservableObject, IDisposable
{
    private readonly IPerformanceProfilingService _profiling;
    private readonly IEnterpriseMaintenanceService _maintenance;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    public EnterpriseMaintenanceViewModel(
        IPerformanceProfilingService profiling,
        IEnterpriseMaintenanceService maintenance,
        IAuthenticationAuthorizationService auth)
    {
        _profiling = profiling;
        _maintenance = maintenance;
        _auth = auth;
        FleetRows = new ObservableCollection<TerminalFleetStatusRowViewModel>();
        MaintenanceHistory = new ObservableCollection<MaintenanceHistoryRowViewModel>();

        _profiling.SnapshotUpdated += OnSnapshotUpdated;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _refreshTimer.Tick += async (_, _) => await RefreshMetricsAsync().ConfigureAwait(true);
        _refreshTimer.Start();

        _ = InitializeAsync();
    }

    public ObservableCollection<TerminalFleetStatusRowViewModel> FleetRows { get; }

    public ObservableCollection<MaintenanceHistoryRowViewModel> MaintenanceHistory { get; }

    [ObservableProperty]
    private double _cpuUsagePercentage;

    [ObservableProperty]
    private long _memoryConsumptionMb;

    [ObservableProperty]
    private int _averageQueryLatencyMs;

    [ObservableProperty]
    private double _uiFramesPerSecond;

    [ObservableProperty]
    private string _terminalFleetStatus = "Loading fleet status…";

    [ObservableProperty]
    private int _errorsLastHour;

    [ObservableProperty]
    private string _statusMessage = "Enterprise maintenance dashboard ready.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _supervisorOverride;

    public void NotifyUiFrameRendered() => _profiling.RecordRenderedFrame();

    private async Task InitializeAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ExecuteEnterpriseMaintenance);
            await RefreshMetricsAsync().ConfigureAwait(true);
            await LoadFleetAsync().ConfigureAwait(true);
            await LoadHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private void OnSnapshotUpdated(object? sender, PerformanceProfileSnapshot snapshot)
    {
        ApplySnapshot(snapshot);
    }

    [RelayCommand]
    private async Task RefreshMetricsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ExecuteEnterpriseMaintenance);
            var snapshot = await _profiling.CaptureSnapshotAsync().ConfigureAwait(true);
            ApplySnapshot(snapshot);
            await LoadFleetAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ClearCachesAsync() => await RunMaintenanceAsync(EnterpriseMaintenanceCommandTypes.ClearCaches).ConfigureAwait(true);

    [RelayCommand]
    private async Task ReorganizeIndexesAsync() =>
        await RunMaintenanceAsync(EnterpriseMaintenanceCommandTypes.ReorganizeIndexes).ConfigureAwait(true);

    [RelayCommand]
    private async Task RenewCredentialsAsync() =>
        await RunMaintenanceAsync(EnterpriseMaintenanceCommandTypes.RenewMraCredentials).ConfigureAwait(true);

    [RelayCommand]
    private async Task FlushTelemetryAsync() =>
        await RunMaintenanceAsync(EnterpriseMaintenanceCommandTypes.FlushTelemetry).ConfigureAwait(true);

    private async Task RunMaintenanceAsync(string command)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _auth.EnsurePermission(OperatorPermissions.ExecuteEnterpriseMaintenance);
            StatusMessage = $"Running {command}…";
            var result = await _maintenance
                .ExecuteCommandAsync(command, SupervisorOverride)
                .ConfigureAwait(true);
            StatusMessage = result.Message;
            await LoadHistoryAsync().ConfigureAwait(true);
            await RefreshMetricsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplySnapshot(PerformanceProfileSnapshot snapshot)
    {
        CpuUsagePercentage = snapshot.CpuUsagePercentage;
        MemoryConsumptionMb = snapshot.MemoryConsumptionMb;
        AverageQueryLatencyMs = snapshot.AverageQueryLatencyMs;
        UiFramesPerSecond = snapshot.UiFramesPerSecond;
        ErrorsLastHour = snapshot.ErrorsLastHour;
    }

    private async Task LoadFleetAsync()
    {
        var fleet = await _profiling.GetFleetStatusAsync().ConfigureAwait(true);
        FleetRows.Clear();
        var healthy = 0;
        foreach (var row in fleet)
        {
            if (string.Equals(row.Status, "Healthy", StringComparison.OrdinalIgnoreCase))
            {
                healthy++;
            }

            FleetRows.Add(new TerminalFleetStatusRowViewModel
            {
                TerminalId = row.TerminalId,
                BranchId = row.BranchId,
                Status = row.Status,
                CpuUsagePercentage = row.CpuUsagePercentage,
                MemoryConsumptionMb = row.MemoryConsumptionMb,
                AverageQueryLatencyMs = row.AverageQueryLatencyMs,
                LastSeenUtc = row.LastSeenUtc,
                IsLocalTerminal = row.IsLocalTerminal
            });
        }

        TerminalFleetStatus = fleet.Count == 0
            ? "No terminals reporting."
            : $"{healthy}/{fleet.Count} terminals healthy";
    }

    private async Task LoadHistoryAsync()
    {
        var rows = await _maintenance.GetRecentResultsAsync().ConfigureAwait(true);
        MaintenanceHistory.Clear();
        foreach (var row in rows)
        {
            MaintenanceHistory.Add(new MaintenanceHistoryRowViewModel
            {
                CommandType = row.CommandType,
                Success = row.Success,
                Message = row.Message,
                ExecutedAtUtc = row.ExecutedAtUtc,
                DurationMs = row.DurationMs
            });
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
        _profiling.SnapshotUpdated -= OnSnapshotUpdated;
    }
}

public sealed class TerminalFleetStatusRowViewModel
{
    public string TerminalId { get; init; } = string.Empty;
    public string BranchId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public double CpuUsagePercentage { get; init; }
    public long MemoryConsumptionMb { get; init; }
    public int AverageQueryLatencyMs { get; init; }
    public DateTime LastSeenUtc { get; init; }
    public bool IsLocalTerminal { get; init; }
}

public sealed class MaintenanceHistoryRowViewModel
{
    public string CommandType { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime ExecutedAtUtc { get; init; }
    public long DurationMs { get; init; }
}
