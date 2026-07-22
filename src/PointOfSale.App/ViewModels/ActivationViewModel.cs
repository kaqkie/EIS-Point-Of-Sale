using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Light-themed terminal license activation prior to operator sign-in.
/// </summary>
public partial class ActivationViewModel : ObservableObject
{
    private readonly ITerminalActivationService _activation;

    public ActivationViewModel(ITerminalActivationService activation)
    {
        _activation = activation;
        _ = RefreshAsync();
    }

    [ObservableProperty]
    private string _licenseKey = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Enter your Albert Retail Terminal license key to unlock this register.";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isActivated;

    [ObservableProperty]
    private string? _maskedLicenseKey;

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
        try
        {
            var result = await _activation.ActivateAsync(LicenseKey).ConfigureAwait(true);
            StatusMessage = result.Message;
            if (result.Success)
            {
                LicenseKey = string.Empty;
                await RefreshAsync().ConfigureAwait(true);
            }
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
