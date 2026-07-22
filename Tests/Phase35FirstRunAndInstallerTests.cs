using System.IO;
using PointOfSale.App.Deployment;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Constants;
using PointOfSale.Core.Pricing;
using Xunit;

namespace PointOfSale.Tests;

public sealed class Phase35FirstRunAndInstallerTests
{
    [Fact]
    public void InstallerConfiguration_BuildsExpressAndLocalDbConnectionStrings()
    {
        var express = InstallerConfiguration.BuildSqlExpressConnectionString();
        Assert.Contains("SQLEXPRESS", express, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PointOfSale", express, StringComparison.OrdinalIgnoreCase);

        var localDb = InstallerConfiguration.BuildLocalDbConnectionString();
        Assert.Contains("(localdb)", localDb, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MSSQLLocalDB", localDb, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstallerConfiguration_SqlExpressSilentArgs_AreQuietAndAcceptLicense()
    {
        var args = InstallerConfiguration.BuildSqlExpressSilentInstallArguments();
        Assert.Contains("/QUIET", args, StringComparison.Ordinal);
        Assert.Contains("IACCEPTSQLSERVERLICENSETERMS=1", args, StringComparison.Ordinal);
        Assert.Contains("INSTANCENAME=SQLEXPRESS", args, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerConfiguration_ClickOncePublishCommand_TargetsProfile()
    {
        var cmd = InstallerConfiguration.BuildClickOncePublishCommand(@"C:\repo");
        Assert.Contains("PublishProfile=ClickOnceProfile", cmd, StringComparison.Ordinal);
        Assert.Contains("PointOfSale.App.csproj", cmd, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerPackagingOptions_AllowLocalDbFallback_ByDefault()
    {
        var options = new InstallerPackagingOptions();
        Assert.True(options.AllowLocalDbFallback);
        Assert.Equal("SQLEXPRESS", options.SqlExpressInstanceName);
        Assert.Equal("MSSQLLocalDB", options.LocalDbInstanceName);
    }

    [Fact]
    public void DeploymentConfigurationKeys_IncludeFirstRunFields()
    {
        Assert.Equal("FirstRun.Completed", DeploymentConfigurationKeys.FirstRunCompleted);
        Assert.Equal("deployment.terminal.displayName", DeploymentConfigurationKeys.TerminalDisplayName);
        Assert.Equal("deployment.branchId", DeploymentConfigurationKeys.BranchId);
        Assert.Equal("FirstRun.MraEnvironment", DeploymentConfigurationKeys.MraEnvironmentPreference);
    }

    [Fact]
    public void FirstRunSetupRequest_DefaultsToSandbox()
    {
        var request = new FirstRunSetupRequest
        {
            TerminalDisplayName = "Counter 1",
            BranchId = "LLW-01"
        };
        Assert.Equal("Sandbox", request.MraEnvironment);
        Assert.Equal(17.5m, PosTaxCalculator.MalawiStandardVatRatePercent);
    }

    [Fact]
    public void SqlEngineKind_HasExpressAndLocalDb()
    {
        Assert.Equal(1, (int)SqlEngineKind.SqlExpress);
        Assert.Equal(2, (int)SqlEngineKind.LocalDb);
    }

    [Fact]
    public void ProductInstallerArtifacts_ExistOnDisk()
    {
        var repoRoot = FindRepoRoot();
        Assert.True(File.Exists(Path.Combine(repoRoot, "Setup", "ProductInstaller.wxs")));
        Assert.True(File.Exists(Path.Combine(repoRoot, "Setup", "Bootstrap-SqlExpressOrLocalDb.ps1")));
        Assert.True(File.Exists(Path.Combine(repoRoot, "Setup", "FIRST-RUN.md")));
        Assert.True(File.Exists(Path.Combine(repoRoot, "Scripts", "022_FirstRunSetupWizard.sql")));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PointOfSale.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate PointOfSale.sln from test base directory.");
    }
}
