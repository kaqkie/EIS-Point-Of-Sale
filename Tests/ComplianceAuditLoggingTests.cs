using PointOfSale.App.Options;
using PointOfSale.Core.Compliance;
using PointOfSale.Infrastructure.Services;
using PointOfSale.Mra.Options;
using Xunit;

namespace PointOfSale.Tests;

public sealed class ComplianceAuditLoggingTests
{
    [Fact]
    public void MraProductionHandshakeOptions_DefaultWarningWindow()
    {
        var options = new MraProductionHandshakeOptions();
        Assert.True(options.Enabled);
        Assert.True(options.CertificateWarningDays >= 7);
    }

    [Fact]
    public void MraRuntimeEnvironmentState_ResolvesProductionBaseUrl()
    {
        var state = new MraRuntimeEnvironmentState();
        state.ApplyHandshake("Production", DateTime.UtcNow, DateTime.UtcNow.AddDays(30));

        var options = new MraApiOptions
        {
            Environment = "Sandbox",
            // Legacy unreachable host must be rewritten to eis-api.mra.mw
            ProductionBaseUrl = "https://apis.mra.mw/api/v1/",
            SandboxBaseUrl = "https://dev-eis-api.mra.mw/api/v1/",
            BaseUrl = "https://apis.mra.mw/api/v1/"
        };

        Assert.True(state.IsLiveProductionActive(options));
        Assert.Contains("eis-api.mra.mw", state.GetEffectiveBaseUrl(options), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("apis.mra.mw", state.GetEffectiveBaseUrl(options), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MraApiOptions_RewritesLegacyUnreachableHosts()
    {
        Assert.True(MraApiOptions.IsLegacyUnreachableHost("https://apis.mra.mw/api/v1/"));
        Assert.Contains(
            "eis-api.mra.mw",
            MraApiOptions.NormalizeBaseUrl("https://apis.mra.mw/api/v1/"),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "https://dev-eis-api.mra.mw/api/v1/sales/submit-sales-transaction",
            MraApiOptions.CombineEndpoint("https://dev-eis-api.mra.mw/api/v1/", "/sales/submit-sales-transaction").ToString());
        // Host-only sandbox root must expand to /api/v1/.
        Assert.Equal(
            MraApiOptions.DefaultSandboxBaseUrl,
            MraApiOptions.NormalizeBaseUrl("https://dev-eis-api.mra.mw/"));
        Assert.Equal(
            "https://dev-eis-api.mra.mw/api/v1/sales/submit-sales-transaction",
            MraApiOptions.CombineEndpoint("https://dev-eis-api.mra.mw/", "sales/submit-sales-transaction").ToString());
        // Relative paths that already include api/v1 must not duplicate the segment.
        Assert.Equal(
            "https://dev-eis-api.mra.mw/api/v1/sales/submit-sales-transaction",
            MraApiOptions.CombineEndpoint(
                "https://dev-eis-api.mra.mw/api/v1/",
                "api/v1/sales/submit-sales-transaction").ToString());
        Assert.Equal(
            MraApiOptions.DefaultSandboxBaseUrl,
            MraApiOptions.NormalizeBaseUrl("https://dev-eis-api.mra.mw/api/v1/api/v1/"));
    }

    [Fact]
    public void ComplianceAuditCategories_DefineRequiredEventTypes()
    {
        Assert.False(string.IsNullOrWhiteSpace(ComplianceAuditCategories.TransactionSubmission));
        Assert.False(string.IsNullOrWhiteSpace(ComplianceAuditCategories.OfflineQueue));
        Assert.False(string.IsNullOrWhiteSpace(ComplianceAuditCategories.MraHandshake));
    }
}
