using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Phase 33 disaster-recovery dashboard: automated EOD backups, manual backup/restore, audit log.
/// </summary>
public partial class DatabaseBackupViewModel : ObservableObject
{
    private readonly IDatabaseBackupService _backupService;
    private readonly IDatabaseRestorationService _restorationService;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly IAuditSecurityLogger _auditLogger;

    public DatabaseBackupViewModel(
        IDatabaseBackupService backupService,
        IDatabaseRestorationService restorationService,
        IAuthenticationAuthorizationService auth,
        IAuditSecurityLogger auditLogger)
    {
        _backupService = backupService;
        _restorationService = restorationService;
        _auth = auth;
        _auditLogger = auditLogger;
        History = new ObservableCollection<DatabaseBackupHistoryEntry>();
        StatusAuditLog = new ObservableCollection<string>();

        _backupService.StatusChanged += OnBackupStatusChanged;
        BackupFileLocation = _backupService.BackupFileLocation ?? string.Empty;
        StorageUsageMb = _backupService.GetBackupStorageUsageMb();
        _ = RefreshAsync();
    }

    public ObservableCollection<DatabaseBackupHistoryEntry> History { get; }
    public ObservableCollection<string> StatusAuditLog { get; }

    [ObservableProperty]
    private DateTime? _lastBackupTimestamp;

    [ObservableProperty]
    private string _backupFileLocation = string.Empty;

    [ObservableProperty]
    private bool _isBackupInProgress;

    [ObservableProperty]
    private double _storageUsageMb;

    [ObservableProperty]
    private bool _isRestoring;

    [ObservableProperty]
    private string _backupDirectory = string.Empty;

    [ObservableProperty]
    private string? _selectedBackupPath;

    [ObservableProperty]
    private string _statusMessage = "Loading backup status…";

    [ObservableProperty]
    private string? _lastError;

    [ObservableProperty]
    private bool _confirmRestore;

    private void OnBackupStatusChanged(object? sender, EventArgs e)
    {
        void Apply()
        {
            IsBackupInProgress = _backupService.IsBackupInProgress;
            LastBackupTimestamp = _backupService.LastBackupTimestamp;
            BackupFileLocation = _backupService.BackupFileLocation ?? string.Empty;
            LastError = _backupService.LastError;
            StorageUsageMb = _backupService.GetBackupStorageUsageMb();
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
            var snapshot = await _backupService.GetStatusSnapshotAsync().ConfigureAwait(true);
            IsBackupInProgress = snapshot.IsBackupInProgress;
            LastBackupTimestamp = snapshot.LastBackupTimestamp;
            BackupFileLocation = snapshot.BackupFileLocation ?? string.Empty;
            BackupDirectory = snapshot.BackupDirectory;
            StorageUsageMb = snapshot.StorageUsageMb;
            LastError = snapshot.LastError;

            History.Clear();
            foreach (var entry in await _backupService.GetHistoryAsync().ConfigureAwait(true))
            {
                History.Add(entry);
            }

            StatusMessage = History.Count == 0
                ? "No backups recorded yet. Run a manual backup or wait for the end-of-day schedule."
                : $"Loaded {History.Count} backup history entr(y/ies). Storage {StorageUsageMb:N2} MB.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LastError = ex.Message;
            PushLog($"REFRESH ERROR — {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (IsBackupInProgress || IsRestoring)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.TriggerBackup);
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = ex.Message;
            PushLog($"BACKUP DENIED — {ex.Message}");
            return;
        }

        StatusMessage = "Starting SQL Express end-of-day-capable backup…";
        try
        {
            var result = await _backupService.BackupNowAsync(DatabaseBackupTriggers.Manual).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            if (result.Success && result.Manifest is not null)
            {
                BackupFileLocation = result.Manifest.BackupFilePath;
                LastBackupTimestamp = result.Manifest.CreatedAtUtc;
                PushLog($"BACKUP OK — {result.Manifest.BackupFilePath} ({result.Manifest.BackupBytes:N0} bytes)");
                StatusMessage = result.Message ?? "Backup completed.";
                await _auditLogger.LogAsync(
                        SecurityAuditActions.BackupTriggered,
                        detail: result.Manifest.BackupFilePath,
                        success: true,
                        operatorId: _auth.CurrentOperator?.OperatorId,
                        username: _auth.CurrentOperator?.Username)
                    .ConfigureAwait(true);
            }
            else
            {
                LastError = result.Error;
                PushLog($"BACKUP FAILED — {result.Error}");
                StatusMessage = result.Error ?? "Backup failed.";
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
            PushLog($"BACKUP ERROR — {ex.Message}");
        }
    }

    [RelayCommand]
    private void BrowseBackup()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SQL Backup (*.bak)|*.bak|All files (*.*)|*.*",
            Title = "Select SQL Express backup for disaster recovery",
            InitialDirectory = Directory.Exists(BackupDirectory)
                ? BackupDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedBackupPath = dialog.FileName;
            StatusMessage = $"Selected {SelectedBackupPath}";
            PushLog($"SELECTED — {SelectedBackupPath}");
        }
    }

    [RelayCommand]
    private void UseHistoryBackup(DatabaseBackupHistoryEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.BackupFilePath))
        {
            return;
        }

        SelectedBackupPath = entry.BackupFilePath;
        StatusMessage = $"Selected history backup {entry.BackupFilePath}";
        PushLog($"HISTORY SELECT — {entry.BackupFilePath}");
    }

    [RelayCommand]
    private async Task VerifyBackupAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedBackupPath))
        {
            StatusMessage = "Select a .bak file first.";
            return;
        }

        try
        {
            var result = await _restorationService.VerifyBackupAsync(SelectedBackupPath).ConfigureAwait(true);
            StatusMessage = result.Success
                ? result.Message ?? "Backup verification passed."
                : result.Error ?? "Verification failed.";
            PushLog(result.Success
                ? $"VERIFY OK — {SelectedBackupPath}"
                : $"VERIFY FAILED — {result.Error}");
            if (!result.Success)
            {
                LastError = result.Error;
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
            PushLog($"VERIFY ERROR — {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (IsBackupInProgress || IsRestoring)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(SelectedBackupPath))
        {
            StatusMessage = "Select a .bak file first.";
            return;
        }

        if (!ConfirmRestore)
        {
            StatusMessage = "Confirm destructive restore before recovering onto this terminal.";
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.TriggerBackup);
        }
        catch (UnauthorizedAccessException ex)
        {
            StatusMessage = ex.Message;
            PushLog($"RESTORE DENIED — {ex.Message}");
            return;
        }

        IsRestoring = true;
        StatusMessage = "Restoring database — POS will be unavailable until complete…";
        PushLog($"RESTORE START — {SelectedBackupPath}");
        try
        {
            var result = await _restorationService.RestoreAsync(SelectedBackupPath, confirmDestructive: true)
                .ConfigureAwait(true);
            if (result.Success)
            {
                PushLog(
                    $"RESTORE OK — preserved {result.PreservedQueueItems}, re-queued {result.RestoredQueueItems}. {result.Message}");
                StatusMessage = result.Message ?? "Restore completed.";
                ConfirmRestore = false;
                await RefreshAsync().ConfigureAwait(true);
            }
            else
            {
                LastError = result.Error;
                PushLog($"RESTORE FAILED — {result.Error}");
                StatusMessage = result.Error ?? "Restore failed.";
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
            PushLog($"RESTORE ERROR — {ex.Message}");
        }
        finally
        {
            IsRestoring = false;
        }
    }

    private void PushLog(string message)
    {
        StatusAuditLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — {message}");
        while (StatusAuditLog.Count > 80)
        {
            StatusAuditLog.RemoveAt(StatusAuditLog.Count - 1);
        }
    }
}
