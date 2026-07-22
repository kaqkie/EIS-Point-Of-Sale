using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class HardwareManagementViewModel : ObservableObject, IDisposable
{
    private readonly IHardwarePeripheralService _hardware;
    private readonly IMultiTerminalSyncBroker _syncBroker;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly Timer _refreshTimer;
    private bool _disposed;

    public HardwareManagementViewModel(
        IHardwarePeripheralService hardware,
        IMultiTerminalSyncBroker syncBroker,
        IAuthenticationAuthorizationService auth)
    {
        _hardware = hardware;
        _syncBroker = syncBroker;
        _auth = auth;
        PeerTerminals = new ObservableCollection<TerminalPeerRowViewModel>();
        RecentMessages = new ObservableCollection<string>();

        _hardware.PeripheralStatusChanged += OnPeripheralStatusChanged;
        _hardware.BarcodeScanned += OnBarcodeScanned;
        _syncBroker.StatusChanged += OnSyncStatusChanged;

        _refreshTimer = new Timer(
            _ =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null)
                {
                    return;
                }

                _ = dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await RefreshAsync().ConfigureAwait(true);
                    }
                    catch
                    {
                        // Timer refresh is advisory; status message is set inside RefreshAsync.
                    }
                });
            },
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(12));
        _ = InitializeAsync();
    }

    public ObservableCollection<TerminalPeerRowViewModel> PeerTerminals { get; }
    public ObservableCollection<string> RecentMessages { get; }

    [ObservableProperty]
    private bool _isPrinterConnected;

    [ObservableProperty]
    private bool _isCashDrawerReady;

    [ObservableProperty]
    private string _scannerStatus = "Unknown";

    [ObservableProperty]
    private DateTime? _lastPeripheralCheckTimestamp;

    [ObservableProperty]
    private string? _lastHardwareError;

    [ObservableProperty]
    private string _multiTerminalStatus = "Loading…";

    [ObservableProperty]
    private int _onlineTerminalCount;

    [ObservableProperty]
    private int _pendingLedgerCount;

    [ObservableProperty]
    private int _pendingOfflineInvoices;

    [ObservableProperty]
    private string _localTerminalId = string.Empty;

    [ObservableProperty]
    private string _branchId = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = "Loading hardware diagnostics…";

    [ObservableProperty]
    private string _lastScannedBarcode = string.Empty;

    private async Task InitializeAsync()
    {
        try
        {
            await _hardware.StartScannerMonitoringAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            PushMessage($"Scanner start: {ex.Message}");
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            EnsureCanView();
            var health = await _hardware.ProbeAsync().ConfigureAwait(true);
            ApplyHardware(health);

            var sync = await _syncBroker.GetStatusAsync().ConfigureAwait(true);
            ApplySync(sync);
            StatusMessage = "Hardware and multi-terminal status refreshed.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LastHardwareError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task TestPrintAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Sending ESC/POS hardware test page…";
        try
        {
            EnsureCanManage();
            await _hardware.PrintTestReceiptAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            PushMessage($"{DateTime.Now:HH:mm:ss} — Test print OK (auto-cut + high-density QR).");
            StatusMessage = "Test print completed.";
        }
        catch (Exception ex)
        {
            LastHardwareError = ex.Message;
            StatusMessage = ex.Message;
            PushMessage($"{DateTime.Now:HH:mm:ss} — Test print FAILED: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task KickCashDrawerAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Triggering cash drawer kick…";
        try
        {
            _auth.EnsurePermission(OperatorPermissions.OpenCashDrawer);
            await _hardware.KickCashDrawerAsync().ConfigureAwait(true);
            IsCashDrawerReady = _hardware.IsCashDrawerReady;
            PushMessage($"{DateTime.Now:HH:mm:ss} — Cash drawer kick sent.");
            StatusMessage = "Cash drawer kick completed.";
        }
        catch (Exception ex)
        {
            LastHardwareError = ex.Message;
            StatusMessage = ex.Message;
            PushMessage($"{DateTime.Now:HH:mm:ss} — Drawer kick FAILED: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReconnectPeripheralsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Reconnecting peripherals…";
        try
        {
            EnsureCanManage();
            await _hardware.ReconnectAllAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            PushMessage($"{DateTime.Now:HH:mm:ss} — Reconnect cycle completed.");
            StatusMessage = "Peripherals reconnected.";
        }
        catch (Exception ex)
        {
            LastHardwareError = ex.Message;
            StatusMessage = ex.Message;
            PushMessage($"{DateTime.Now:HH:mm:ss} — Reconnect FAILED: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SyncTerminalsNowAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Running multi-terminal synchronize…";
        try
        {
            EnsureCanManage();
            var result = await _syncBroker.SynchronizeNowAsync().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            var detail = result.Error ?? result.Message ?? "Sync complete.";
            PushMessage(
                $"{DateTime.Now:HH:mm:ss} — Multi-terminal: applied {result.AppliedLedgerCount}, " +
                $"inventory events {result.InventoryDeltasApplied}. {detail}");
            StatusMessage = detail;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            PushMessage($"{DateTime.Now:HH:mm:ss} — Multi-terminal ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyHardware(HardwarePeripheralHealthSnapshot health)
    {
        IsPrinterConnected = health.IsPrinterConnected;
        IsCashDrawerReady = health.IsCashDrawerReady;
        ScannerStatus = health.ScannerStatus;
        LastPeripheralCheckTimestamp = health.CheckedAtUtc.ToLocalTime();
        LastHardwareError = health.LastError;
    }

    private void ApplySync(MultiTerminalSyncStatusSnapshot sync)
    {
        LocalTerminalId = sync.TerminalId;
        BranchId = sync.BranchId;
        OnlineTerminalCount = sync.OnlineTerminalCount;
        PendingLedgerCount = sync.PendingLedgerCount;
        PendingOfflineInvoices = sync.PendingOfflineInvoices;
        MultiTerminalStatus = sync.ConnectionStatusText;

        PeerTerminals.Clear();
        foreach (var peer in sync.Peers)
        {
            PeerTerminals.Add(TerminalPeerRowViewModel.From(peer));
        }
    }

    private void OnPeripheralStatusChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            IsPrinterConnected = _hardware.IsPrinterConnected;
            IsCashDrawerReady = _hardware.IsCashDrawerReady;
            ScannerStatus = _hardware.ScannerStatus;
            LastPeripheralCheckTimestamp = _hardware.LastPeripheralCheckTimestamp?.ToLocalTime();
            LastHardwareError = _hardware.LastError;
        }

        Dispatch(Apply);
    }

    private void OnBarcodeScanned(object? sender, string barcode)
    {
        Dispatch(() =>
        {
            LastScannedBarcode = barcode;
            PushMessage($"{DateTime.Now:HH:mm:ss} — Scan: {barcode}");
        });
    }

    private void OnSyncStatusChanged(object? sender, EventArgs e)
    {
        Dispatch(() => MultiTerminalStatus = _syncBroker.ConnectionStatusText);
    }

    private void EnsureCanView() =>
        _auth.EnsurePermission(OperatorPermissions.ManageHardwarePeripherals);

    private void EnsureCanManage()
    {
        EnsureCanView();
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private void PushMessage(string message)
    {
        RecentMessages.Insert(0, message);
        while (RecentMessages.Count > 50)
        {
            RecentMessages.RemoveAt(RecentMessages.Count - 1);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Dispose();
        _hardware.PeripheralStatusChanged -= OnPeripheralStatusChanged;
        _hardware.BarcodeScanned -= OnBarcodeScanned;
        _syncBroker.StatusChanged -= OnSyncStatusChanged;
        _ = _hardware.StopScannerMonitoringAsync();
    }
}

public sealed class TerminalPeerRowViewModel
{
    public string TerminalId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string HostName { get; init; } = string.Empty;
    public DateTime LastSeenUtc { get; init; }
    public int PendingOfflineInvoices { get; init; }
    public decimal OpenShiftExpectedCash { get; init; }
    public string OpenShiftCashier { get; init; } = string.Empty;

    public static TerminalPeerRowViewModel From(TerminalHeartbeatRow row) => new()
    {
        TerminalId = row.TerminalId,
        Status = row.Status,
        HostName = row.HostName ?? string.Empty,
        LastSeenUtc = row.LastSeenUtc,
        PendingOfflineInvoices = row.PendingOfflineInvoices,
        OpenShiftExpectedCash = row.OpenShiftExpectedCash,
        OpenShiftCashier = row.OpenShiftCashier ?? string.Empty
    };
}
