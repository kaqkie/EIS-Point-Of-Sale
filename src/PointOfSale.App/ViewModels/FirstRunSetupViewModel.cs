using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Phase 35 light-themed first-run onboarding wizard for store managers.
/// </summary>
public partial class FirstRunSetupViewModel : ObservableObject
{
    private readonly IFirstRunBootstrapService _bootstrap;
    private readonly ITerminalActivationService _activation;

    public FirstRunSetupViewModel(
        IFirstRunBootstrapService bootstrap,
        ITerminalActivationService activation)
    {
        _bootstrap = bootstrap;
        _activation = activation;
        _ = InitializeAsync();
    }

    public string[] MraEnvironmentOptions { get; } = ["Sandbox", "Production"];

    public string SampleLicenseHint => TerminalActivationService.SampleLicenseKey;

    [ObservableProperty]
    private int _wizardStep;

    [ObservableProperty]
    private string _terminalDisplayName = "Albert Retail Counter";

    [ObservableProperty]
    private string _branchId = string.Empty;

    [ObservableProperty]
    private string _siteId = string.Empty;

    [ObservableProperty]
    private string _mraEnvironment = "Sandbox";

    [ObservableProperty]
    private string _licenseKey = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Welcome — configure this register for Malawi MRA EIS operations.";

    [ObservableProperty]
    private string _infrastructureSummary = "Checking SQL Express / LocalDB…";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _infrastructureReady;

    [RelayCommand]
    private async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _bootstrap.EnsureInfrastructureAsync().ConfigureAwait(true);
            InfrastructureReady = result.Success;
            InfrastructureSummary = result.Success
                ? $"{result.Message} Engine={result.DetectedEngine}; VAT 17.5% seeded."
                : result.Message;
            StatusMessage = result.Success
                ? "Infrastructure ready. Continue with terminal identity."
                : result.Message;
        }
        catch (Exception ex)
        {
            InfrastructureReady = false;
            InfrastructureSummary = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void NextStep()
    {
        if (WizardStep == 0 && !InfrastructureReady)
        {
            StatusMessage = "Resolve SQL Express / LocalDB before continuing.";
            return;
        }

        if (WizardStep == 1)
        {
            if (string.IsNullOrWhiteSpace(TerminalDisplayName) || string.IsNullOrWhiteSpace(BranchId))
            {
                StatusMessage = "Enter a terminal display name and branch ID.";
                return;
            }
        }

        if (WizardStep < 3)
        {
            WizardStep++;
            StatusMessage = WizardStep switch
            {
                1 => "Name this counter and assign the branch / outlet code.",
                2 => "Select the MRA EIS endpoint environment for this store.",
                3 => "Enter the Albert Retail Terminal license key to unlock the register.",
                _ => StatusMessage
            };
        }
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (WizardStep > 0)
        {
            WizardStep--;
        }
    }

    [RelayCommand]
    private void PasteSampleLicense() => LicenseKey = TerminalActivationService.SampleLicenseKey;

    [RelayCommand]
    private async Task FinishAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!InfrastructureReady)
        {
            StatusMessage = "Infrastructure is not ready. Resolve SQL Express/LocalDB, then retry.";
            return;
        }

        if (string.IsNullOrWhiteSpace(TerminalDisplayName) || string.IsNullOrWhiteSpace(BranchId))
        {
            WizardStep = 1;
            StatusMessage = "Terminal name and branch ID are required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(LicenseKey))
        {
            WizardStep = 3;
            StatusMessage = "A license key is required to complete first-run setup.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Saving first-run configuration…";
        try
        {
            var result = await _bootstrap.CompleteSetupAsync(
                    new FirstRunSetupRequest
                    {
                        TerminalDisplayName = TerminalDisplayName.Trim(),
                        BranchId = BranchId.Trim(),
                        SiteId = string.IsNullOrWhiteSpace(SiteId) ? null : SiteId.Trim(),
                        MraEnvironment = MraEnvironment,
                        LicenseKey = LicenseKey.Trim()
                    })
                .ConfigureAwait(true);

            StatusMessage = result.Message;
            if (result.Success)
            {
                LicenseKey = string.Empty;
                await _activation.GetStatusAsync().ConfigureAwait(true);
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
    private async Task RetryInfrastructureAsync() => await InitializeAsync().ConfigureAwait(true);
}
