using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Phase 35/39 light-themed first-run onboarding wizard for store managers.
/// Final step hides Next and promotes Finish setup as the primary CTA.
/// </summary>
public partial class FirstRunSetupViewModel : ObservableObject
{
    private readonly IFirstRunBootstrapService _bootstrap;
    private readonly ITerminalActivationService _activation;
    private readonly IMraOnboardingService _mraOnboarding;

    public FirstRunSetupViewModel(
        IFirstRunBootstrapService bootstrap,
        ITerminalActivationService activation,
        IMraOnboardingService mraOnboarding)
    {
        _bootstrap = bootstrap;
        _activation = activation;
        _mraOnboarding = mraOnboarding;
        _ = InitializeAsync();
    }

    public string[] MraEnvironmentOptions { get; } = ["Sandbox", "Production"];

    public string SampleLicenseHint => TerminalActivationService.SampleLicenseKey;

    /// <summary>True on Step 3 (License Activation &amp; Finalization) — Next is hidden.</summary>
    public bool IsFinalStep => WizardStep >= 3;

    /// <summary>True while intermediate wizard steps allow forward navigation.</summary>
    public bool CanGoNext => WizardStep < 3 && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinalStep))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(StepCaption))]
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
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _infrastructureReady;

    public string StepCaption => IsFinalStep
        ? "Step 3 of 3 — License activation & finalization"
        : $"Step {WizardStep} of 3";

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
        if (IsFinalStep)
        {
            StatusMessage = "This is the final step — use Finish setup to complete onboarding.";
            return;
        }

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

        WizardStep++;
        StatusMessage = WizardStep switch
        {
            1 => "Name this counter and assign the branch / outlet code.",
            2 => "Select the MRA EIS endpoint environment for this store.",
            3 => "Enter the activation key, then choose Finish setup.",
            _ => StatusMessage
        };
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
            StatusMessage = "An activation key is required to complete first-run setup.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Contacting MRA EIS onboarding, then saving first-run configuration…";
        try
        {
            var mra = await _mraOnboarding
                .ActivateAndConfirmAsync(LicenseKey.Trim(), BranchId.Trim())
                .ConfigureAwait(true);
            if (!mra.Success)
            {
                WizardStep = 3;
                StatusMessage = mra.Message;
                return;
            }

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

            if (!result.Success)
            {
                StatusMessage = result.Message;
                return;
            }

            LicenseKey = string.Empty;
            await _activation.GetStatusAsync().ConfigureAwait(true);
            StatusMessage = mra.UsedSandboxLocalFallback
                ? $"{result.Message} Sandbox MRA terminal {mra.TerminalId} staged."
                : $"{result.Message} MRA terminal {mra.TerminalId} confirmed.";
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
