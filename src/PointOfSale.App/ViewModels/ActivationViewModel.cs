using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Phase 38 light-themed terminal activation — ART license + MRA EIS onboarding.
/// </summary>
public partial class ActivationViewModel : ObservableObject
{
    private readonly ITerminalActivationService _activation;
    private readonly IMraOnboardingService _mraOnboarding;

    public ActivationViewModel(
        ITerminalActivationService activation,
        IMraOnboardingService mraOnboarding)
    {
        _activation = activation;
        _mraOnboarding = mraOnboarding;
        _ = RefreshAsync();
    }

    [ObservableProperty]
    private string _licenseKey = string.Empty;

    [ObservableProperty]
    private string _statusMessage =
        "Enter your Albert Retail Terminal activation key to bind this register to MRA EIS and unlock sign-in.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isActivated;

    [ObservableProperty]
    private string? _maskedLicenseKey;

    [ObservableProperty]
    private string? _mraTerminalId;

    [ObservableProperty]
    private string _sampleHint = TerminalActivationService.SampleLicenseKey;

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

    [RelayCommand]
    private async Task ActivateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Validating activation key and contacting MRA EIS onboarding…";
        try
        {
            if (!_activation.ValidateLicenseKeyFormat(LicenseKey, out var normalized, out var formatError))
            {
                StatusMessage = formatError ?? "Invalid activation key format.";
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
                StatusMessage = mra.Message;
                return;
            }

            MraTerminalId = mra.TerminalId;

            var license = await _activation.ActivateAsync(normalized).ConfigureAwait(true);
            if (!license.Success)
            {
                StatusMessage = license.Message;
                return;
            }

            LicenseKey = string.Empty;
            await RefreshAsync().ConfigureAwait(true);

            StatusMessage = mra.UsedSandboxLocalFallback
                ? $"{license.Message} MRA sandbox credentials stored for terminal {mra.TerminalId}."
                : $"{license.Message} MRA terminal {mra.TerminalId} confirmed — you can sign in.";
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

    [RelayCommand]
    private void PasteSampleKey() => LicenseKey = TerminalActivationService.SampleLicenseKey;
}
