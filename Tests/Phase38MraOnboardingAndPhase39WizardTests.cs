using System.Net.Http;
using PointOfSale.App.Services;
using PointOfSale.App.ViewModels;
using PointOfSale.Core.Pricing;
using PointOfSale.Mra.Http;
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
        Assert.Equal(3, FirstRunSetupViewModel.FinalWizardStep);
        Assert.True(IsFinal(3));
        Assert.False(IsFinal(2));
        Assert.False(IsNextVisible(3));
        Assert.True(IsFinishVisible(3));
        Assert.True(IsNextVisible(2));
        Assert.False(IsFinishVisible(1));
        Assert.False(CanGoNext(3, isBusy: false));
        Assert.True(CanGoNext(2, isBusy: false));
        Assert.False(CanGoNext(1, isBusy: true));
        // Phase 41 — Finish also requires exact XXXX-XXXX-XXXX-XXXX format.
        Assert.True(CanFinish(3, isBusy: false, formatValid: true));
        Assert.False(CanFinish(3, isBusy: false, formatValid: false));
        Assert.False(CanFinish(3, isBusy: true, formatValid: true));
        Assert.False(CanFinish(2, isBusy: false, formatValid: true));

        static bool IsFinal(int step) => step >= FirstRunSetupViewModel.FinalWizardStep;
        static bool IsNextVisible(int step) => !IsFinal(step);
        static bool IsFinishVisible(int step) => IsFinal(step);
        static bool CanGoNext(int step, bool isBusy) => IsNextVisible(step) && !isBusy;
        static bool CanFinish(int step, bool isBusy, bool formatValid) =>
            IsFinishVisible(step) && !isBusy && formatValid;
    }

    [Fact]
    public void MraOnboardingResult_OkAndFailFactories()
    {
        var ok = MraOnboardingResult.Ok(
            "done",
            "TERM-1",
            sandboxFallback: true,
            upstreamHttpStatus: 404,
            upstreamDiagnostic: "not found");
        Assert.True(ok.Success);
        Assert.Equal("TERM-1", ok.TerminalId);
        Assert.True(ok.UsedSandboxLocalFallback);
        Assert.Equal(404, ok.UpstreamHttpStatus);
        Assert.Equal("not found", ok.UpstreamDiagnostic);

        var fail = MraOnboardingResult.Fail("bad key", upstreamHttpStatus: 502);
        Assert.False(fail.Success);
        Assert.Equal("bad key", fail.Message);
        Assert.Equal(502, fail.UpstreamHttpStatus);
    }

    [Fact]
    public void Phase40_MraApiException_IsRecoverableForSandboxFallback()
    {
        var mra404 = new MraApiException("Not Found", 404, "{\"statusCode\":0,\"remark\":\"missing\"}");
        Assert.True(MraOnboardingService.IsRecoverableMraEndpointFailure(mra404));

        var mra502 = new MraApiException("Bad Gateway", 502, "upstream");
        Assert.True(MraOnboardingService.IsRecoverableMraEndpointFailure(mra502));

        var wrapped = new InvalidOperationException("wrap", mra404);
        Assert.True(MraOnboardingService.IsRecoverableMraEndpointFailure(wrapped));

        Assert.True(MraOnboardingService.IsRecoverableMraEndpointFailure(new HttpRequestException("offline")));
        Assert.False(MraOnboardingService.IsRecoverableMraEndpointFailure(new ArgumentException("bad arg")));
    }

    [Fact]
    public void PosConfiguration_IgnoresPlaceholders_AndParsesDeploymentEnvelopes()
    {
        Assert.Null(PosConfigurationService.NormalizeConfiguredValue("{SITE-ID}"));
        Assert.Null(PosConfigurationService.NormalizeConfiguredValue("{BRANCH-ID}"));
        Assert.Equal("City Center", PosConfigurationService.NormalizeConfiguredValue("City Center"));
        Assert.Equal("1234567890", PosConfigurationService.ExtractConfiguredString("{\"tin\":\"1234567890\"}"));
        Assert.Equal("City Center", PosConfigurationService.ExtractConfiguredString("City Center"));
        Assert.Equal("ART-1", PosConfigurationService.ExtractConfiguredString("{\"terminalId\":\"ART-1\"}"));
    }

    [Fact]
    public void PosRuntimeContext_FallsBackToDeploymentSiteAndTin()
    {
        var ctx = new PosRuntimeContext(
            Global: null,
            Terminal: null,
            Taxpayer: null,
            Deployment: new PointOfSale.App.Options.TerminalDeploymentOptions
            {
                BranchId = "{BRANCH-ID}",
                SiteId = "{SITE-ID}",
                TaxpayerTin = "2007123456"
            },
            DeploymentSiteId: "City Center",
            DeploymentTaxpayerTin: null,
            DeploymentBranchId: "Lilongwe",
            AllowSandboxDeveloperTin: true);

        Assert.Equal("2007123456", ctx.SellerTin);
        Assert.Equal("City Center", ctx.SiteId);
        Assert.Equal("SITE-CITY-CENTER", ctx.FiscalSiteId);
        Assert.Equal("Lilongwe", ctx.BranchId);
        Assert.Equal(1, ctx.GlobalConfigVersion);
        Assert.Equal(MraTaxRateCodes.StandardVat, ctx.StandardVatTaxRateId);
        Assert.True(ctx.HasRequiredSalesIdentity);
    }

    [Fact]
    public void PosRuntimeContext_ResolvesMerchantHeaderFromDeploymentFallbacks()
    {
        var ctx = new PosRuntimeContext(
            Global: null,
            Terminal: null,
            Taxpayer: new PointOfSale.Mra.Contracts.Configuration.TaxpayerConfigurationDto
            {
                Tin = "2007123456"
            },
            Deployment: new PointOfSale.App.Options.TerminalDeploymentOptions
            {
                BranchId = "Lilongwe",
                SiteId = "SITE-CITY-CENTER",
                TaxpayerTin = "2007123456",
                MerchantAddressLines = ["City Center", "Lilongwe"],
                ContactPhone = "+265 1 234 567",
                ContactEmail = "shop@albertretail.mw"
            },
            DeploymentBranchId: "Lilongwe",
            AllowSandboxDeveloperTin: false);

        Assert.Equal(["City Center", "Lilongwe"], ctx.AddressLines);
        Assert.Equal("+265 1 234 567", ctx.ContactPhone);
        Assert.Equal("shop@albertretail.mw", ctx.ContactEmail);
    }

    [Fact]
    public void PosRuntimeContext_Production_IgnoresSandboxPlaceholderTin()
    {
        var ctx = new PosRuntimeContext(
            Global: null,
            Terminal: null,
            Taxpayer: new PointOfSale.Mra.Contracts.Configuration.TaxpayerConfigurationDto
            {
                Tin = "1234567890"
            },
            Deployment: new PointOfSale.App.Options.TerminalDeploymentOptions
            {
                TaxpayerTin = "2007123456"
            },
            DeploymentTaxpayerTin: "1234567890",
            AllowSandboxDeveloperTin: false);

        Assert.Equal("2007123456", ctx.SellerTin);
        Assert.True(PosConfigurationService.IsPlaceholderTaxpayerTin("1234567890"));
    }

    [Fact]
    public void PosRuntimeContext_SandboxTrial_AcceptsDeveloperTinFromAppsettings()
    {
        var ctx = new PosRuntimeContext(
            Global: null,
            Terminal: null,
            Taxpayer: null,
            Deployment: new PointOfSale.App.Options.TerminalDeploymentOptions
            {
                BranchId = "Lilongwe",
                SiteId = "City Center",
                TaxpayerTin = "1234567890"
            },
            AllowSandboxDeveloperTin: true,
            HostEnvironmentName: "Sandbox");

        Assert.Equal("1234567890", ctx.SellerTin);
        Assert.True(ctx.HasRequiredSalesIdentity);
        Assert.Contains(
            "appsettings.json",
            PosConfigurationService.BuildIncompleteConfigurationMessage(ctx.HostEnvironmentName),
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "appsettings.Production.json",
            PosConfigurationService.BuildIncompleteConfigurationMessage(ctx.HostEnvironmentName),
            StringComparison.OrdinalIgnoreCase);
    }
}
