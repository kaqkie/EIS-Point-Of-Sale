using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PointOfSale.App.Services;

namespace PointOfSale.App.ViewModels;

/// <summary>
/// Phase 35/39/40 light-themed first-run onboarding wizard for store managers.
/// Dynamic stepper actions: intermediate steps expose Next; Step 3 collapses Next and
/// promotes Finish setup as the sole primary CTA (activation + SQL Express persistence).
/// </summary>
public partial class FirstRunSetupViewModel : ObservableObject
{
    public const int FinalWizardStep = 3;
    public const int TotalWizardSteps = 3;

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

    /// <summary>True on Step 3 of 3 (License Activation &amp; Finalization).</summary>
    public bool IsFinalStep => WizardStep >= FinalWizardStep;

    /// <summary>Phase 40 — Next is only offered on intermediate steps.</summary>
    public bool IsNextButtonVisible => !IsFinalStep;

    /// <summary>Phase 40 — Finish setup is the primary CTA on the final step only.</summary>
    public bool IsFinishButtonVisible => IsFinalStep;

    /// <summary>True while intermediate wizard steps allow forward navigation.</summary>
    public bool CanGoNext => IsNextButtonVisible && !IsBusy;

    /// <summary>Finish is enabled on the final step when the wizard is idle.</summary>
    public bool CanFinish => IsFinishButtonVisible && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinalStep))]
    [NotifyPropertyChangedFor(nameof(IsNextButtonVisible))]
    [NotifyPropertyChangedFor(nameof(IsFinishButtonVisible))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    [NotifyPropertyChangedFor(nameof(StepCaption))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionCaption))]
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
    [NotifyPropertyChangedFor(nameof(CanFinish))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _infrastructureReady;

    public string StepCaption => WizardStep switch
    {
        0 => "Step 1 of 3 — Local database bootstrap",
        1 => "Step 2 of 3 — Terminal identity & MRA environment",
        2 => "Step 2 of 3 — MRA EIS endpoint",
        _ => "Step 3 of 3 — License activation & finalization"
    };

    /// <summary>Dynamic primary action label for the stepper footer.</summary>
    public string PrimaryActionCaption => IsFinalStep ? "Finish setup" : "Next";

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

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextStep()
    {
        if (IsFinalStep || !IsNextButtonVisible)
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

        NextStepCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void PreviousStep()
    {
        if (WizardStep > 0)
        {
            WizardStep--;
            NextStepCommand.NotifyCanExecuteChanged();
            FinishCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void PasteSampleLicense() => LicenseKey = TerminalActivationService.SampleLicenseKey;

    [RelayCommand(CanExecute = nameof(CanFinish))]
    private async Task FinishAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!IsFinalStep)
        {
            WizardStep = FinalWizardStep;
            StatusMessage = "Complete license activation on this final step, then choose Finish setup.";
            FinishCommand.NotifyCanExecuteChanged();
            NextStepCommand.NotifyCanExecuteChanged();
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
            NextStepCommand.NotifyCanExecuteChanged();
            FinishCommand.NotifyCanExecuteChanged();
            return;
        }

        if (string.IsNullOrWhiteSpace(LicenseKey))
        {
            WizardStep = FinalWizardStep;
            StatusMessage = "An activation key is required to complete first-run setup.";
            return;
        }

        IsBusy = true;
        NextStepCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
        StatusMessage = "Contacting MRA EIS onboarding (activate-terminal), then saving first-run configuration to SQL Express…";
        try
        {
            var mra = await _mraOnboarding
                .ActivateAndConfirmAsync(LicenseKey.Trim(), BranchId.Trim())
                .ConfigureAwait(true);
            if (!mra.Success)
            {
                WizardStep = FinalWizardStep;
                StatusMessage = mra.UpstreamHttpStatus is int http
                    ? $"{mra.Message} (HTTP {http})"
                    : mra.Message;
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
                ? $"{result.Message} Sandbox MRA terminal {mra.TerminalId} staged with encrypted TerminalCredentials."
                : $"{result.Message} MRA terminal {mra.TerminalId} confirmed.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            NextStepCommand.NotifyCanExecuteChanged();
            FinishCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task RetryInfrastructureAsync() => await InitializeAsync().ConfigureAwait(true);

    partial void OnWizardStepChanged(int value)
    {
        NextStepCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NextStepCommand.NotifyCanExecuteChanged();
        FinishCommand.NotifyCanExecuteChanged();
    }
}
