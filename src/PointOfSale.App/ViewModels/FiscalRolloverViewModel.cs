using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;

namespace PointOfSale.App.ViewModels;

public partial class FiscalRolloverViewModel : ObservableObject
{
    private readonly IFiscalYearRolloverService _rolloverService;
    private readonly IArchivalCompressionService _archivalService;
    private readonly IAuthenticationAuthorizationService _auth;

    public FiscalRolloverViewModel(
        IFiscalYearRolloverService rolloverService,
        IArchivalCompressionService archivalService,
        IAuthenticationAuthorizationService auth)
    {
        _rolloverService = rolloverService;
        _archivalService = archivalService;
        _auth = auth;
        YearArchives = new ObservableCollection<FiscalYearArchiveRecord>();
        ArchivePackages = new ObservableCollection<FiscalArchivePackageRecord>();
        MissingClosureDates = new ObservableCollection<DateTime>();
        CurrentFiscalYear = _rolloverService.ResolveCurrentFiscalYear();
        RequiresSupervisorAuthorization = true;
        _ = RefreshAsync();
    }

    public ObservableCollection<FiscalYearArchiveRecord> YearArchives { get; }
    public ObservableCollection<FiscalArchivePackageRecord> ArchivePackages { get; }
    public ObservableCollection<DateTime> MissingClosureDates { get; }

    [ObservableProperty]
    private int _currentFiscalYear;

    [ObservableProperty]
    private int _selectedFiscalYear;

    [ObservableProperty]
    private string _rolloverStatusMessage = "Fiscal year rollover and secure archival.";

    [ObservableProperty]
    private bool _isArchivingInProgress;

    [ObservableProperty]
    private bool _requiresSupervisorAuthorization = true;

    [ObservableProperty]
    private string _secondarySupervisorUsername = string.Empty;

    [ObservableProperty]
    private string _secondarySupervisorPassword = string.Empty;

    [ObservableProperty]
    private string _primaryArchivePassword = string.Empty;

    [ObservableProperty]
    private string _secondaryArchivePassword = string.Empty;

    [ObservableProperty]
    private string _rolloverNotes = string.Empty;

    [ObservableProperty]
    private bool _allowClosureGapsOverride;

    [ObservableProperty]
    private decimal _previewGrossMwk;

    [ObservableProperty]
    private decimal _previewVatMwk;

    [ObservableProperty]
    private int _previewClosedDays;

    [ObservableProperty]
    private int _previewExpectedDays;

    [ObservableProperty]
    private bool _canExecuteRollover;

    [ObservableProperty]
    private bool _hasMissingClosureDates;

    partial void OnCurrentFiscalYearChanged(int value) => SelectedFiscalYear = value;

    partial void OnSecondarySupervisorUsernameChanged(string value) => UpdateAuthorizationFlag();

    partial void OnSecondarySupervisorPasswordChanged(string value) => UpdateAuthorizationFlag();

    partial void OnPrimaryArchivePasswordChanged(string value) => UpdateAuthorizationFlag();

    partial void OnSecondaryArchivePasswordChanged(string value) => UpdateAuthorizationFlag();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            _auth.EnsurePermission(OperatorPermissions.ExecuteFiscalYearRollover);
            CurrentFiscalYear = _rolloverService.ResolveCurrentFiscalYear();
            if (SelectedFiscalYear <= 0)
            {
                SelectedFiscalYear = CurrentFiscalYear - 1;
            }

            var preview = await _rolloverService.BuildPreviewAsync(SelectedFiscalYear).ConfigureAwait(true);
            PreviewGrossMwk = preview.TotalGrossSalesMwk;
            PreviewVatMwk = preview.TotalVatCollectedMwk;
            PreviewClosedDays = preview.ClosedDays;
            PreviewExpectedDays = preview.ExpectedBusinessDays;
            CanExecuteRollover = preview.CanRollover;
            RolloverStatusMessage = preview.SummaryMessage;

            MissingClosureDates.Clear();
            foreach (var day in preview.MissingClosureDates)
            {
                MissingClosureDates.Add(day);
            }

            HasMissingClosureDates = MissingClosureDates.Count > 0;

            YearArchives.Clear();
            foreach (var row in await _rolloverService.GetRecentArchivesAsync().ConfigureAwait(true))
            {
                YearArchives.Add(row);
            }

            ArchivePackages.Clear();
            foreach (var pkg in await _archivalService.GetRecentPackagesAsync().ConfigureAwait(true))
            {
                ArchivePackages.Add(pkg);
            }
        }
        catch (Exception ex)
        {
            RolloverStatusMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task ExecuteYearEndRolloverAsync()
    {
        if (IsArchivingInProgress)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ExecuteFiscalYearRollover);
            EnsureDualKeyAuthorizationReady();

            var confirm = MessageBox.Show(
                Application.Current.MainWindow,
                $"Execute fiscal year {SelectedFiscalYear} rollover?\n\n"
                + "This locks the tax year into an encrypted archive. Dual supervisor authorization is recorded.",
                "Confirm fiscal year rollover",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                RolloverStatusMessage = "Rollover cancelled.";
                return;
            }

            IsArchivingInProgress = true;
            var result = await _rolloverService.ExecuteRolloverAsync(
                    SelectedFiscalYear,
                    BuildAuthorization(),
                    RolloverNotes,
                    AllowClosureGapsOverride)
                .ConfigureAwait(true);

            RolloverStatusMessage = result.Message;
            ClearSecrets();
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RolloverStatusMessage = ex.Message;
        }
        finally
        {
            IsArchivingInProgress = false;
        }
    }

    [RelayCommand]
    private async Task ArchiveStaleDataAsync()
    {
        if (IsArchivingInProgress)
        {
            return;
        }

        try
        {
            _auth.EnsurePermission(OperatorPermissions.ExecuteFiscalYearRollover);
            EnsureDualKeyAuthorizationReady();

            var confirm = MessageBox.Show(
                Application.Current.MainWindow,
                "Archive sales, void logs, and telemetry older than 12 months into an encrypted package?",
                "Confirm secure archival",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            IsArchivingInProgress = true;
            var result = await _archivalService.ArchiveStaleDataAsync(BuildAuthorization()).ConfigureAwait(true);
            RolloverStatusMessage = result.Message;
            ClearSecrets();
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RolloverStatusMessage = ex.Message;
        }
        finally
        {
            IsArchivingInProgress = false;
        }
    }

    private FiscalDualKeyAuthorization BuildAuthorization() =>
        new()
        {
            SecondarySupervisorUsername = SecondarySupervisorUsername.Trim(),
            SecondarySupervisorPassword = SecondarySupervisorPassword,
            PrimaryArchivePassword = PrimaryArchivePassword,
            SecondaryArchivePassword = SecondaryArchivePassword
        };

    private void EnsureDualKeyAuthorizationReady()
    {
        if (string.IsNullOrWhiteSpace(SecondarySupervisorUsername)
            || string.IsNullOrWhiteSpace(SecondarySupervisorPassword)
            || string.IsNullOrWhiteSpace(PrimaryArchivePassword)
            || string.IsNullOrWhiteSpace(SecondaryArchivePassword))
        {
            throw new InvalidOperationException(
                "Dual-key authorization required: secondary supervisor credentials and both archive passwords.");
        }

        if (PrimaryArchivePassword.Length < 8 || SecondaryArchivePassword.Length < 8)
        {
            throw new InvalidOperationException("Archive passwords must be at least 8 characters.");
        }
    }

    private void UpdateAuthorizationFlag()
    {
        RequiresSupervisorAuthorization =
            string.IsNullOrWhiteSpace(SecondarySupervisorUsername)
            || string.IsNullOrWhiteSpace(SecondarySupervisorPassword)
            || string.IsNullOrWhiteSpace(PrimaryArchivePassword)
            || string.IsNullOrWhiteSpace(SecondaryArchivePassword);
    }

    private void ClearSecrets()
    {
        SecondarySupervisorPassword = string.Empty;
        PrimaryArchivePassword = string.Empty;
        SecondaryArchivePassword = string.Empty;
        UpdateAuthorizationFlag();
    }
}
