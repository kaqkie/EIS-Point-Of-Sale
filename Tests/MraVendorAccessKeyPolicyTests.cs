using PointOfSale.Mra.Options;
using PointOfSale.Mra.Security;
using PointOfSale.Infrastructure.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class MraVendorAccessKeyPolicyTests
{
    [Fact]
    public void Sandbox_DoesNotRequireAccessKey()
    {
        var options = new MraApiOptions { Environment = "Sandbox", VendorAccessKey = string.Empty };
        Assert.False(MraVendorAccessKeyPolicy.RequiresVendorAccessKey(options));
        Assert.Null(MraVendorAccessKeyPolicy.ResolveForActivateTerminal(options));
    }

    [Fact]
    public void Production_RequiresConfiguredAccessKey()
    {
        var options = new MraApiOptions { Environment = "Production", VendorAccessKey = string.Empty };
        Assert.True(MraVendorAccessKeyPolicy.RequiresVendorAccessKey(options));
        Assert.Throws<InvalidOperationException>(() => MraVendorAccessKeyPolicy.ResolveForActivateTerminal(options));
    }

    [Fact]
    public void Production_ReturnsTrimmedAccessKey()
    {
        var options = new MraApiOptions
        {
            Environment = "Production",
            VendorAccessKey = "  vendor-cert-key-001  "
        };

        Assert.Equal("vendor-cert-key-001", MraVendorAccessKeyPolicy.ResolveForActivateTerminal(options));
        Assert.Equal("x-access-key", MraVendorAccessKeyPolicy.HeaderName);
    }

    [Fact]
    public void Production_RejectsTemplatePlaceholders()
    {
        var options = new MraApiOptions
        {
            Environment = "Production",
            VendorAccessKey = "{YOUR-VENDOR-ACCESS-KEY}"
        };

        Assert.False(MraVendorAccessKeyPolicy.TryResolveForActivateTerminal(options, out _, out var error));
        Assert.Contains("VendorAccessKey", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MraRequestContext_CarriesVendorAccessKeyForProductionActivation()
    {
        var context = new MraRequestContext { VendorAccessKey = "prod-key" };
        Assert.Equal("prod-key", context.VendorAccessKey);
        Assert.Null(context.JwtToken);
        Assert.Null(context.SecretKey);
    }
}
