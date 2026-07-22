using PointOfSale.App.Deployment;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Security;
using Xunit;

namespace PointOfSale.Tests;

public sealed class TerminalProvisioningTests
{
    [Fact]
    public void InstallerConfiguration_ComputesStableHardwareFingerprint()
    {
        var a = InstallerConfiguration.ComputeHardwareFingerprintSha256();
        var b = InstallerConfiguration.ComputeHardwareFingerprintSha256();
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
    }

    [Fact]
    public void InstallerConfiguration_HardwareMatch_IsCaseInsensitive()
    {
        var fingerprint = InstallerConfiguration.ComputeHardwareFingerprintSha256();
        Assert.True(InstallerConfiguration.HardwareFingerprintsMatch(fingerprint.ToUpperInvariant()));
    }

    [Fact]
    public void InstallerConfiguration_StandardDirectoriesIncludeLogsAndBackups()
    {
        var paths = InstallerConfiguration.ResolveStandardDirectoryPaths(new InstallerPackagingOptions());
        Assert.Contains(paths, p => p.EndsWith("Logs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(paths, p => p.EndsWith("Backups", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RolePermissionCatalog_StoreManagerCanProvisionTerminal()
    {
        var manager = RolePermissionCatalog.GetPermissions(OperatorRoles.StoreManager);
        Assert.Contains(OperatorPermissions.ProvisionTerminal, manager);
        Assert.DoesNotContain(
            OperatorPermissions.ProvisionTerminal,
            RolePermissionCatalog.GetPermissions(OperatorRoles.Cashier));
    }

    [Fact]
    public void SqlExpressSilentInstall_IncludesInstanceName()
    {
        var args = InstallerConfiguration.BuildSqlExpressSilentInstallArguments("SQLEXPRESS");
        Assert.Contains("INSTANCENAME=SQLEXPRESS", args);
        Assert.Contains("IACCEPTSQLSERVERLICENSETERMS=1", args);
    }
}
