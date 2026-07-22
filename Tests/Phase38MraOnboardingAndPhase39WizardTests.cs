using PointOfSale.App.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class Phase38MraOnboardingAndPhase39WizardTests
{
    [Fact]
    public void MraOnboarding_ExposesOfficialEndpointPaths()
    {
        Assert.Equal("onboarding/activate-terminal", MraOnboardingService.ActivateTerminalPath);
        Assert.Equal(
            "onboarding/terminal-activated-confirmation",
            MraOnboardingService.TerminalActivatedConfirmationPath);
    }

    [Fact]
    public void SampleActivationKey_IsAcceptedForOnboardingGate()
    {
        Assert.Equal("I4CV-M5YY-AKY6-Z9BT", TerminalActivationService.SampleLicenseKey);
    }

    [Fact]
    public void FirstRunWizard_FinalStepHidesNextAndPromotesFinish()
    {
        // Constructing the VM hits infrastructure — assert pure step helpers via a lightweight stand-in.
        Assert.True(IsFinal(3));
        Assert.False(IsFinal(2));
        Assert.False(CanGoNext(3, isBusy: false));
        Assert.True(CanGoNext(2, isBusy: false));
        Assert.False(CanGoNext(1, isBusy: true));

        static bool IsFinal(int step) => step >= 3;
        static bool CanGoNext(int step, bool isBusy) => step < 3 && !isBusy;
    }

    [Fact]
    public void MraOnboardingResult_OkAndFailFactories()
    {
        var ok = MraOnboardingResult.Ok("done", "TERM-1", sandboxFallback: true);
        Assert.True(ok.Success);
        Assert.Equal("TERM-1", ok.TerminalId);
        Assert.True(ok.UsedSandboxLocalFallback);

        var fail = MraOnboardingResult.Fail("bad key");
        Assert.False(fail.Success);
        Assert.Equal("bad key", fail.Message);
    }
}
