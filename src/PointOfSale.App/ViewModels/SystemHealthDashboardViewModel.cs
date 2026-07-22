using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Phase 36 administrative diagnostic dashboard — live SQL, internet, MRA, disk, and sync queue health.
/// </summary>
public partial class SystemHealthDashboardViewModel : ObservableObject
{
    private readonly ISystemHealthMonitorService _healthMonitor;
    private readonly IAuthenticationAuthorizationService _auth;

    public SystemHealthDashboardViewModel(
        ISystemHealthMonitorService healthMonitor,
        IAuthenticationAuthorizationService auth)
    {
        _healthMonitor = healthMonitor;
        _auth = auth;
        RecentAlerts = new ObservableCollection<SystemHealthAlert>();
        WarningMessages = new ObservableCollection<string>();

        _healthMonitor.StatusChanged += OnStatusChanged;
        _healthMonitor.AlertRaised += OnAlertRaised;
        ApplySnapshot(_healthMonitor.LatestSnapshot);
        foreach (var alert in _healthMonitor.RecentAlerts)
        {
            RecentAlerts.Add(alert);
        }

        _ = RefreshAsync();
    }

    public ObservableCollection<SystemHealthAlert> RecentAlerts { get; }
    public ObservableCollection<string> WarningMessages { get; }

    [ObservableProperty]
    private bool _isDatabaseHealthy = true;

    [ObservableProperty]
    private bool _isInternetOnline = true;

    [ObservableProperty]
    private bool _isMraApiReachable = true;

    [ObservableProperty]
    private double _diskSpaceFreeMb;

    [ObservableProperty]
    private int _activeSyncQueueCount;

    [ObservableProperty]
    private int _databaseLatencyMs;

    [ObservableProperty]
    private string _mraApiStatus = "Unknown";

    [ObservableProperty]
    private int? _mraPingMs;

    [ObservableProperty]
    private double _backupVolumeFreeMb;

    [ObservableProperty]
    private string _backupDirectory = string.Empty;

    [ObservableProperty]
    private int _pendingQueueCount;

    [ObservableProperty]
    private int _quarantinedQueueCount;

    [ObservableProperty]
    private bool _overallHealthy = true;

    [ObservableProperty]
    private string _healthSummary = "Run a diagnostic to refresh live health.";

    [ObservableProperty]
    private string _statusMessage = "System health dashboard.";

    [ObservableProperty]
    private bool _isRunningDiagnostics;

    [ObservableProperty]
    private DateTime? _lastCheckedAtUtc;

    [ObservableProperty]
    private Brush _databaseStatusBrush = Brushes.Transparent;

    [ObservableProperty]
    private Brush _internetStatusBrush = Brushes.Transparent;

    [ObservableProperty]
    private Brush _mraStatusBrush = Brushes.Transparent;

    [ObservableProperty]
    private Brush _diskStatusBrush = Brushes.Transparent;

    [ObservableProperty]
    private Brush _queueStatusBrush = Brushes.Transparent;

    private void OnStatusChanged(object? sender, SystemHealthMonitorSnapshot snapshot)
    {
        void Apply() => ApplySnapshot(snapshot);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            dispatcher.Invoke(Apply);
        }
    }

    private void OnAlertRaised(object? sender, SystemHealthAlert alert)
    {
        void Apply()
        {
            RecentAlerts.Insert(0, alert);
            while (RecentAlerts.Count > 40)
            {
                RecentAlerts.RemoveAt(RecentAlerts.Count - 1);
            }

            StatusMessage = $"Alert: {alert.Code} — {alert.Message}";
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            dispatcher.Invoke(Apply);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ViewSystemDiagnostics);
            ApplySnapshot(_healthMonitor.LatestSnapshot);
            RecentAlerts.Clear();
            foreach (var alert in _healthMonitor.RecentAlerts)
            {
                RecentAlerts.Add(alert);
            }

            StatusMessage = RecentAlerts.Count == 0
                ? "Showing latest health snapshot."
                : $"Loaded {RecentAlerts.Count} recent alert(s).";
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
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = ex.Message;
            return;
        }

        IsRunningDiagnostics = true;
        StatusMessage = "Running full system health diagnostic…";
        try
        {
            var snapshot = await _healthMonitor.RunManualDiagnosticAsync().ConfigureAwait(true);
            ApplySnapshot(snapshot);
            StatusMessage = snapshot.OverallHealthy
                ? "Diagnostic complete — all subsystems healthy."
                : $"Diagnostic complete — {snapshot.Summary}";
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

    private void ApplySnapshot(SystemHealthMonitorSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        IsDatabaseHealthy = snapshot.IsDatabaseHealthy;
        IsInternetOnline = snapshot.IsInternetOnline;
        IsMraApiReachable = snapshot.IsMraApiReachable;
        DiskSpaceFreeMb = snapshot.DiskSpaceFreeMb;
        ActiveSyncQueueCount = snapshot.ActiveSyncQueueCount;
        DatabaseLatencyMs = snapshot.DatabaseLatencyMs;
        MraApiStatus = snapshot.MraApiStatus;
        MraPingMs = snapshot.MraPingMs;
        BackupVolumeFreeMb = snapshot.BackupVolumeFreeMb;
        BackupDirectory = snapshot.BackupDirectory;
        PendingQueueCount = snapshot.PendingQueueCount;
        QuarantinedQueueCount = snapshot.QuarantinedQueueCount;
        OverallHealthy = snapshot.OverallHealthy;
        HealthSummary = snapshot.Summary;
        LastCheckedAtUtc = snapshot.CheckedAtUtc;

        WarningMessages.Clear();
        foreach (var warning in snapshot.WarningMessages)
        {
            WarningMessages.Add(warning);
        }

        DatabaseStatusBrush = StatusBrush(snapshot.IsDatabaseHealthy);
        InternetStatusBrush = StatusBrush(snapshot.IsInternetOnline);
        MraStatusBrush = StatusBrush(snapshot.IsMraApiReachable || !snapshot.IsInternetOnline);
        DiskStatusBrush = StatusBrush(snapshot.IsDiskHealthy);
        QueueStatusBrush = StatusBrush(snapshot.IsQueueHealthy);
    }

    private static Brush StatusBrush(bool healthy)
    {
        var key = healthy ? "Art.Brush.SuccessSoft" : "Art.Brush.DangerSoft";
        if (Application.Current?.TryFindResource(key) is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(
            healthy
                ? Color.FromRgb(0xD1, 0xFA, 0xE5)
                : Color.FromRgb(0xFE, 0xE2, 0xE2));
    }
}
