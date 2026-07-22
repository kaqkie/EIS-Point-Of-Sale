using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Compliance;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class ComplianceAuditViewModel : ObservableObject, IDisposable
{
    private readonly IMraProductionHandshakeService _handshake;
    private readonly IComplianceAuditLogger _auditLogger;
    private readonly IAuthenticationAuthorizationService _auth;
    private readonly DispatcherTimer _refreshTimer;
    private bool _disposed;

    public ComplianceAuditViewModel(
        IMraProductionHandshakeService handshake,
        IComplianceAuditLogger auditLogger,
        IAuthenticationAuthorizationService auth)
    {
        _handshake = handshake;
        _auditLogger = auditLogger;
        _auth = auth;
        AuditRows = new ObservableCollection<ComplianceAuditRowViewModel>();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _refreshTimer.Tick += async (_, _) => await RefreshAsync().ConfigureAwait(true);
        _refreshTimer.Start();
        _ = InitializeAsync();
    }

    public ObservableCollection<ComplianceAuditRowViewModel> AuditRows { get; }

    [ObservableProperty]
    private bool _isLiveProductionActive;

    [ObservableProperty]
    private string _certificateExpirationDate = "Unknown";

    [ObservableProperty]
    private string _lastSuccessfulMraSync = "Never";

    [ObservableProperty]
    private string _tamperCheckStatus = "Checking…";

    [ObservableProperty]
    private string _statusMessage = "Loading compliance status…";

    [ObservableProperty]
    private bool _fiscalLockoutActive;

    [ObservableProperty]
    private string _effectiveEndpoint = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.AccessCompliance);
            var status = await _handshake.GetStatusAsync().ConfigureAwait(true);
            ApplyStatus(status);
            var tamper = await _auditLogger.VerifyChainAsync().ConfigureAwait(true);
            TamperCheckStatus = tamper.IsValid ? tamper.Message : $"TAMPER: {tamper.Message}";

            var rows = await _auditLogger.GetRecentAsync(80).ConfigureAwait(true);
            AuditRows.Clear();
            foreach (var row in rows)
            {
                AuditRows.Add(ComplianceAuditRowViewModel.From(row));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ActivateProductionAsync() => await RunHandshakeActionAsync(
        () => _handshake.ActivateProductionHandshakeAsync(),
        "Activating production handshake…").ConfigureAwait(true);

    [RelayCommand]
    private async Task RenewCertificateAsync() => await RunHandshakeActionAsync(
        () => _handshake.RenewCredentialsAsync(),
        "Renewing MRA credentials…").ConfigureAwait(true);

    [RelayCommand]
    private async Task ValidateCertificateAsync() => await RunHandshakeActionAsync(
        () => _handshake.ValidateCertificateChainAsync(),
        "Validating certificate chain…").ConfigureAwait(true);

    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _auth.EnsurePermission(OperatorPermissions.AccessCompliance);
            StatusMessage = "Exporting statutory compliance log…";
            var path = await _handshake.ExportStatutoryComplianceLogAsync().ConfigureAwait(true);
            StatusMessage = $"Compliance log exported to {path}";
            await RefreshAsync().ConfigureAwait(true);
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

    private async Task InitializeAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.AccessCompliance);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private async Task RunHandshakeActionAsync(
        Func<Task<MraHandshakeStatus>> action,
        string runningMessage)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            _auth.EnsurePermission(OperatorPermissions.AccessCompliance);
            StatusMessage = runningMessage;
            var status = await action().ConfigureAwait(true);
            ApplyStatus(status);
            StatusMessage = "Operation completed.";
            await RefreshAsync().ConfigureAwait(true);
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

    private void ApplyStatus(MraHandshakeStatus status)
    {
        IsLiveProductionActive = status.IsLiveProductionActive;
        FiscalLockoutActive = status.FiscalLockoutActive;
        EffectiveEndpoint = status.EffectiveBaseUrl;
        CertificateExpirationDate = status.CertificateExpirationDateUtc?.ToLocalTime().ToString("g") ?? "Unknown";
        LastSuccessfulMraSync = status.LastSuccessfulMraSyncUtc?.ToLocalTime().ToString("g") ?? "Never";
        TamperCheckStatus = status.TamperCheckStatus;
        StatusMessage = status.CertificateWarning ?? (status.IsLiveProductionActive
            ? "Live MRA EIS production endpoint is active."
            : "Terminal is not in live production mode.");
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

public sealed class ComplianceAuditRowViewModel
{
    public long EntryId { get; init; }
    public string CreatedAtLocal { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Detail { get; init; } = string.Empty;

    public static ComplianceAuditRowViewModel From(ComplianceAuditLogEntry entry) =>
        new()
        {
            EntryId = entry.EntryId,
            CreatedAtLocal = entry.CreatedAtUtc.ToLocalTime().ToString("g"),
            Category = entry.Category,
            Action = entry.Action,
            Success = entry.Success,
            Detail = entry.Detail
        };
}
