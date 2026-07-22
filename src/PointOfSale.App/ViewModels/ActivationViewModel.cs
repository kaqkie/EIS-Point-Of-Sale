using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Phase 40/41 terminal activation — ART license + MRA EIS onboarding with
/// masked license key input (auto-hyphen, uppercase, exact format regex).
/// </summary>
public partial class ActivationViewModel : ObservableObject
{
    private readonly ITerminalActivationService _activation;
    private readonly IMraOnboardingService _mraOnboarding;
    private bool _isFormattingLicenseKey;

    public ActivationViewModel(
        ITerminalActivationService activation,
        IMraOnboardingService mraOnboarding)
    {
        _activation = activation;
        _mraOnboarding = mraOnboarding;
        _ = RefreshAsync();
    }

    public string LicenseKeyPlaceholder => LicenseKeyInputFormatter.Placeholder;

    public bool IsLicenseKeyEmpty => string.IsNullOrEmpty(LicenseKey);

    public bool IsLicenseKeyFormatValid => LicenseKeyInputFormatter.IsExactFormat(LicenseKey);

    public bool HasLicenseKeyFormatError => LicenseKeyInputFormatter.ShouldShowFormatError(LicenseKey);

    public bool IsLicenseKeyIncomplete => LicenseKeyInputFormatter.IsIncomplete(LicenseKey);

    public string? LicenseKeyFormatFeedback => LicenseKeyInputFormatter.GetLiveFeedbackMessage(LicenseKey);

    public bool CanActivate => !IsBusy && IsLicenseKeyFormatValid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLicenseKeyEmpty))]
    [NotifyPropertyChangedFor(nameof(IsLicenseKeyFormatValid))]
    [NotifyPropertyChangedFor(nameof(HasLicenseKeyFormatError))]
    [NotifyPropertyChangedFor(nameof(IsLicenseKeyIncomplete))]
    [NotifyPropertyChangedFor(nameof(LicenseKeyFormatFeedback))]
    [NotifyPropertyChangedFor(nameof(CanActivate))]
    private string _licenseKey = string.Empty;

    [ObservableProperty]
    private string _statusMessage =
        "Enter your Albert Retail Terminal activation key to bind this register to MRA EIS and unlock sign-in.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanActivate))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isActivated;

    [ObservableProperty]
    private string? _maskedLicenseKey;

    [ObservableProperty]
    private string? _mraTerminalId;

    [ObservableProperty]
    private string _sampleHint = TerminalActivationService.SampleLicenseKey;

    [ObservableProperty]
    private bool _usedSandboxFallback;

    partial void OnLicenseKeyChanged(string value)
    {
        if (_isFormattingLicenseKey)
        {
            return;
        }

        var formatted = LicenseKeyInputFormatter.ApplyMask(value);
        if (!string.Equals(formatted, value, StringComparison.Ordinal))
        {
            _isFormattingLicenseKey = true;
            try
            {
                LicenseKey = formatted;
            }
            finally
            {
                _isFormattingLicenseKey = false;
            }
        }

        ActivateCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var status = await _activation.GetStatusAsync().ConfigureAwait(true);
            IsActivated = status.IsActivated;
            MaskedLicenseKey = status.MaskedLicenseKey;
            StatusMessage = status.StatusText;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanActivate))]
    private async Task ActivateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ActivateCommand.NotifyCanExecuteChanged();
        UsedSandboxFallback = false;
        StatusMessage = "Validating activation key and contacting MRA EIS onboarding (activate-terminal)…";
        try
        {
            if (!_activation.ValidateLicenseKeyFormat(LicenseKey, out var normalized, out var formatError))
            {
                StatusMessage = formatError ?? LicenseKeyInputFormatter.FormatErrorMessage;
                return;
            }

            if (!_activation.AcceptsLicenseKey(normalized))
            {
                StatusMessage =
                    "Activation key is not valid. Check the key and try again (format I4CV-M5YY-AKY6-Z9BT).";
                return;
            }

            var mra = await _mraOnboarding.ActivateAndConfirmAsync(normalized).ConfigureAwait(true);
            if (!mra.Success)
            {
                StatusMessage = FormatFailure(mra);
                return;
            }

            MraTerminalId = mra.TerminalId;
            UsedSandboxFallback = mra.UsedSandboxLocalFallback;

            var license = await _activation.ActivateAsync(normalized).ConfigureAwait(true);
            if (!license.Success)
            {
                StatusMessage = license.Message;
                return;
            }

            LicenseKey = string.Empty;
            await RefreshAsync().ConfigureAwait(true);

            StatusMessage = mra.UsedSandboxLocalFallback
                ? $"{license.Message} Sandbox/mock MRA path stored encrypted TerminalCredentials for {mra.TerminalId}."
                  + FormatUpstreamHint(mra)
                : $"{license.Message} MRA terminal {mra.TerminalId} confirmed — you can sign in.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            ActivateCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void PasteSampleKey() => LicenseKey = TerminalActivationService.SampleLicenseKey;

    partial void OnIsBusyChanged(bool value) => ActivateCommand.NotifyCanExecuteChanged();

    private static string FormatFailure(MraOnboardingResult mra)
    {
        if (mra.UpstreamHttpStatus is int http)
        {
            return $"{mra.Message} (HTTP {http})";
        }

        return mra.Message;
    }

    private static string FormatUpstreamHint(MraOnboardingResult mra)
    {
        if (mra.UpstreamHttpStatus is int http)
        {
            return $" Upstream EIS returned HTTP {http}.";
        }

        return string.Empty;
    }
}
