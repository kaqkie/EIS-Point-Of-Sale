using System.Text;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;
using Xunit;

namespace PointOfSale.Tests;

public sealed class FiscalYearArchiveTests
{
    [Fact]
    public void FiscalArchivePasswordCipher_DualKeyIsDeterministicAndDistinct()
    {
        var keyA = FiscalArchivePasswordCipher.DeriveDualKey("primary-secret", "secondary-secret");
        var keyB = FiscalArchivePasswordCipher.DeriveDualKey("primary-secret", "secondary-secret");
        var keyC = FiscalArchivePasswordCipher.DeriveDualKey("other-primary", "secondary-secret");

        Assert.Equal(keyA, keyB);
        Assert.NotEqual(keyA, keyC);
        Assert.Equal(32, keyA.Length);
    }

    [Fact]
    public void FiscalArchivePasswordCipher_BuildArtFiscalFile_HasMagicHeader()
    {
        var key = FiscalArchivePasswordCipher.DeriveDualKey("archive-one", "archive-two");
        var manifest = Encoding.UTF8.GetBytes("{\"fiscalYear\":2024}");
        var payload = Encoding.UTF8.GetBytes("{\"invoices\":[]}");
        var file = FiscalArchivePasswordCipher.BuildArtFiscalFile(manifest, payload, key);

        var header = Encoding.ASCII.GetString(file, 0, 8);
        Assert.Equal("ARTFIS1\n", header);
        Assert.True(file.Length > manifest.Length + payload.Length);
    }

    [Fact]
    public void RolePermissionCatalog_StoreManagerCanExecuteFiscalYearRollover()
    {
        var manager = RolePermissionCatalog.GetPermissions(OperatorRoles.StoreManager);
        Assert.Contains(OperatorPermissions.ExecuteFiscalYearRollover, manager);
        Assert.DoesNotContain(
            OperatorPermissions.ExecuteFiscalYearRollover,
            RolePermissionCatalog.GetPermissions(OperatorRoles.Cashier));
    }

    [Fact]
    public void FiscalArchivalOptions_DefaultsMatchMalawiTaxYear()
    {
        var options = new FiscalArchivalOptions();
        Assert.Equal(7, options.FiscalYearStartMonth);
        Assert.Equal(1, options.FiscalYearStartDay);
        Assert.Equal(12, options.StaleDataAgeMonths);
        Assert.True(options.RequireAllDailyClosures);
    }
}
