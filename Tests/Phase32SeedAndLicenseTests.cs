using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PointOfSale.App.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class Phase32SeedAndLicenseTests
{
    [Fact]
    public void FormatExampleKey_MatchesRequiredFormat()
    {
        var service = CreateService(requireActivation: true);
        Assert.True(service.ValidateLicenseKeyFormat(
            "ABCD-EFGH-IJKL-MNOP",
            out var normalized,
            out var error));
        Assert.Null(error);
        Assert.Equal("ABCD-EFGH-IJKL-MNOP", normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("XXXX-XXXX-XXXX")]
    [InlineData("!!!!-!!!!-!!!!-!!!!")]
    public void InvalidLicenseFormats_AreRejected(string key)
    {
        var service = CreateService(requireActivation: true);
        Assert.False(service.ValidateLicenseKeyFormat(key, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void FormerSampleLicenseKey_IsNotHardcodedAccepted()
    {
        var service = CreateService(requireActivation: true);
        Assert.False(service.AcceptsLicenseKey("I4CV-M5YY-AKY6-Z9BT"));
    }

    [Fact]
    public void ChecksumValidKey_IsAccepted()
    {
        var pepper = "AlbertRetailTerminal.License.v1";
        var payload = "ABCD1234WXYZ";
        var check = TerminalActivationService.ComputeChecksumGroup(payload, pepper);
        var key = $"ABCD-1234-WXYZ-{check}";
        var service = CreateService(requireActivation: true, pepper: pepper);
        Assert.True(service.AcceptsLicenseKey(key));
    }

    [Fact]
    public void ChecksumInvalidKey_IsRejected()
    {
        var service = CreateService(requireActivation: true);
        Assert.False(service.AcceptsLicenseKey("ABCD-1234-WXYZ-0000"));
    }

    [Fact]
    public void MaskKey_HidesMiddleGroups()
    {
        var masked = TerminalActivationService.MaskKey("ABCD-EFGH-IJKL-MNOP");
        Assert.Equal("ABCD-****-****-MNOP", masked);
    }

    [Fact]
    public void DefaultSeedCredentials_MatchPhase32Spec()
    {
        Assert.Equal("admin", PointOfSale.App.Database.Seeders.InitialDataSeeder.DefaultAdminUsername);
        Assert.Equal("admin123", PointOfSale.App.Database.Seeders.InitialDataSeeder.DefaultAdminPassword);
        Assert.Equal("cashier", PointOfSale.App.Database.Seeders.InitialDataSeeder.DefaultCashierUsername);
        Assert.Equal("cashier123", PointOfSale.App.Database.Seeders.InitialDataSeeder.DefaultCashierPassword);
    }

    [Fact]
    public void Pbkdf2_HashesSeedPasswords()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var (hash, salt, iterations) = hasher.HashPassword("admin123");
        Assert.True(hasher.VerifyPassword("admin123", hash, salt, iterations));
        Assert.False(hasher.VerifyPassword("cashier123", hash, salt, iterations));
    }

    private static TerminalActivationService CreateService(bool requireActivation, string? pepper = null)
    {
        var options = Options.Create(new TerminalLicenseOptions
        {
            RequireActivation = requireActivation,
            VerificationPepper = pepper ?? "AlbertRetailTerminal.License.v1"
        });

        // Scope factory unused for format/checksum unit paths.
        var scopeFactory = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();

        return new TerminalActivationService(
            options,
            scopeFactory,
            NullLogger<TerminalActivationService>.Instance);
    }
}
