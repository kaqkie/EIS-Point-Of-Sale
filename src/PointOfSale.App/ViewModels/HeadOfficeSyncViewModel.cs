using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;

namespace PointOfSale.App.ViewModels;

public partial class HeadOfficeSyncViewModel : ObservableObject
{
    private readonly IHeadOfficeSyncService _syncService;
    private readonly Timer _refreshTimer;

    public HeadOfficeSyncViewModel(IHeadOfficeSyncService syncService)
    {
        _syncService = syncService;
        RecentMessages = new ObservableCollection<string>();
        _syncService.StatusChanged += OnSyncStatusChanged;

        _refreshTimer = new Timer(
            async _ => await RefreshAsync().ConfigureAwait(false),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(15));
        _ = RefreshAsync();
    }

    public ObservableCollection<string> RecentMessages { get; }

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private DateTime? _lastSyncTimestamp;

    [ObservableProperty]
    private int _pendingUploadCount;

    [ObservableProperty]
    private int _failedUploadCount;

    [ObservableProperty]
    private bool _isHeadOfficeReachable;

    [ObservableProperty]
    private bool _isNetworkAvailable;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private DateTime? _lastCatalogPullUtc;

    [ObservableProperty]
    private string _connectionStatusText = "Loading…";

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    private void OnSyncStatusChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            IsSyncing = _syncService.IsSyncing;
            LastSyncTimestamp = _syncService.LastSyncTimestamp;
            PendingUploadCount = _syncService.PendingUploadCount;
            IsHeadOfficeReachable = _syncService.IsHeadOfficeReachable;
            ConnectionStatusText = _syncService.ConnectionStatusText;
            LastError = _syncService.LastError;
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
            var snapshot = await _syncService.GetStatusAsync().ConfigureAwait(true);
            ApplySnapshot(snapshot);
            StatusMessage = snapshot.Enabled
                ? "Monitoring branch ↔ head-office replication health."
                : "HeadOfficeSync is disabled. Enable it in appsettings to start cloud replication.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LastError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (IsSyncing)
        {
            return;
        }

        StatusMessage = "Starting manual head-office sync…";
        try
        {
            var result = await _syncService.SyncNowAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);

            var detail = result.Error
                ?? result.Message
                ?? (result.SkippedOffline ? "Queued offline." : "Sync completed.");
            PushMessage(
                $"{DateTime.Now:HH:mm:ss} — packaged {result.PackagedCount}, uploaded {result.UploadedCount}, " +
                $"catalog {result.CatalogProductsApplied} (stock preserved {result.ConflictsPreservedLocalStock}). {detail}");
            StatusMessage = detail;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LastError = ex.Message;
            PushMessage($"{DateTime.Now:HH:mm:ss} — ERROR: {ex.Message}");
        }
    }

    private void ApplySnapshot(HeadOfficeSyncStatusSnapshot snapshot)
    {
        IsEnabled = snapshot.Enabled;
        IsSyncing = snapshot.IsSyncing;
        LastSyncTimestamp = snapshot.LastSyncTimestampUtc;
        PendingUploadCount = snapshot.PendingUploadCount;
        FailedUploadCount = snapshot.FailedUploadCount;
        IsHeadOfficeReachable = snapshot.IsHeadOfficeReachable;
        IsNetworkAvailable = snapshot.IsNetworkAvailable;
        LastCatalogPullUtc = snapshot.LastCatalogPullUtc;
        ConnectionStatusText = snapshot.ConnectionStatusText;
        LastError = snapshot.LastError;
    }

    private void PushMessage(string message)
    {
        RecentMessages.Insert(0, message);
        while (RecentMessages.Count > 40)
        {
            RecentMessages.RemoveAt(RecentMessages.Count - 1);
        }
    }
}
