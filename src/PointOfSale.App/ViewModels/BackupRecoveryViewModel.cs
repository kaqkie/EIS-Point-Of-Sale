using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PointOfSale.App.Services;

namespace PointOfSale.App.ViewModels;

public partial class BackupRecoveryViewModel : ObservableObject
{
    private readonly IDatabaseBackupService _backupService;
    private readonly IDatabaseRestorationService _restorationService;

    public BackupRecoveryViewModel(
        IDatabaseBackupService backupService,
        IDatabaseRestorationService restorationService)
    {
        _backupService = backupService;
        _restorationService = restorationService;
        History = new ObservableCollection<DatabaseBackupHistoryEntry>();
        ActivityLog = new ObservableCollection<string>();

        _backupService.StatusChanged += OnBackupStatusChanged;
        BackupDirectory = _backupService.ResolveBackupDirectory();
        _ = RefreshAsync();
    }

    public ObservableCollection<DatabaseBackupHistoryEntry> History { get; }
    public ObservableCollection<string> ActivityLog { get; }

    [ObservableProperty]
    private bool _isBackingUp;

    [ObservableProperty]
    private bool _isRestoring;

    [ObservableProperty]
    private DateTime? _lastBackupTime;

    [ObservableProperty]
    private string? _backupFilePath;

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
            IsBackingUp = _backupService.IsBackingUp;
            LastBackupTime = _backupService.LastBackupTime;
            BackupFilePath = _backupService.BackupFilePath;
            LastError = _backupService.LastError;
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
            IsBackingUp = _backupService.IsBackingUp;
            LastBackupTime = _backupService.LastBackupTime;
            BackupFilePath = _backupService.BackupFilePath;
            LastError = _backupService.LastError;
            BackupDirectory = _backupService.ResolveBackupDirectory();

            History.Clear();
            foreach (var entry in await _backupService.GetHistoryAsync().ConfigureAwait(true))
            {
                History.Add(entry);
            }

            StatusMessage = History.Count == 0
                ? "No backups recorded yet. Run a manual backup to create the first snapshot."
                : $"Loaded {History.Count} backup history entr(y/ies).";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            LastError = ex.Message;
        }
    }

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        if (IsBackingUp || IsRestoring)
        {
            return;
        }

        StatusMessage = "Starting SQL Express backup…";
        try
        {
            var result = await _backupService.BackupNowAsync(DatabaseBackupTriggers.Manual).ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            if (result.Success && result.Manifest is not null)
            {
                BackupFilePath = result.Manifest.BackupFilePath;
                LastBackupTime = result.Manifest.CreatedAtUtc;
                PushLog($"Backup OK — {result.Manifest.BackupFilePath} ({result.Manifest.BackupBytes:N0} bytes)");
                StatusMessage = result.Message ?? "Backup completed.";
            }
            else
            {
                LastError = result.Error;
                PushLog($"Backup FAILED — {result.Error}");
                StatusMessage = result.Error ?? "Backup failed.";
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            StatusMessage = ex.Message;
            PushLog($"Backup ERROR — {ex.Message}");
        }
    }

    [RelayCommand]
    private void BrowseBackup()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "SQL Backup (*.bak)|*.bak|All files (*.*)|*.*",
            Title = "Select SQL Express backup to restore",
            InitialDirectory = Directory.Exists(BackupDirectory)
                ? BackupDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedBackupPath = dialog.FileName;
            StatusMessage = $"Selected {SelectedBackupPath}";
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
        if (IsBackingUp || IsRestoring)
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
            StatusMessage = "Tick 'I confirm destructive restore' before recovering.";
            return;
        }

        IsRestoring = true;
        StatusMessage = "Restoring database — POS will be unavailable until complete…";
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
        ActivityLog.Insert(0, $"{DateTime.Now:HH:mm:ss} — {message}");
        while (ActivityLog.Count > 50)
        {
            ActivityLog.RemoveAt(ActivityLog.Count - 1);
        }
    }
}
