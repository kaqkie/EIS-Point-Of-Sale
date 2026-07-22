using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using PointOfSale.Infrastructure.Repositories;
using Xunit;

namespace PointOfSale.Tests;

public sealed class SystemDiagnosticsTests
{
    [Fact]
    public void SystemHealthSnapshot_OverallHealthy_RequiresAllSubsystems()
    {
        var healthy = new SystemHealthSnapshot
        {
            IsDatabaseHealthy = true,
            IsDiskHealthy = true,
            IsPrinterHealthy = true,
            IsMraHealthy = true
        };
        Assert.True(healthy.OverallHealthy);

        healthy.IsMraHealthy = false;
        Assert.False(healthy.OverallHealthy);
    }

    [Fact]
    public void DiagnosticDetailJson_SerializesPayload()
    {
        var json = DiagnosticDetailJson.Serialize(new { latencyMs = 42, success = true });
        Assert.Contains("latencyMs", json);
        Assert.Contains("42", json);
    }

    [Fact]
    public void RolePermissionCatalog_StoreManagerCanViewDiagnostics()
    {
        var permissions = RolePermissionCatalog.GetPermissions(OperatorRoles.StoreManager);
        Assert.Contains(OperatorPermissions.ViewSystemDiagnostics, permissions);
    }

    [Fact]
    public void RolePermissionCatalog_CashierCannotViewDiagnostics()
    {
        var permissions = RolePermissionCatalog.GetPermissions(OperatorRoles.Cashier);
        Assert.DoesNotContain(OperatorPermissions.ViewSystemDiagnostics, permissions);
    }

    [Fact]
    public void SystemDiagnosticsOptions_DefaultsAreTerminalSafe()
    {
        var options = new SystemDiagnosticsOptions();
        Assert.True(options.Enabled);
        Assert.True(options.HealthCheckIntervalSeconds >= 15);
        Assert.True(options.MinimumFreeDiskMegabytes >= 100);
        Assert.True(options.TelemetryRetentionDays >= 7);
        Assert.False(string.IsNullOrWhiteSpace(options.DiagnosticLogDirectory));
    }

    [Fact]
    public void DiagnosticEventCategories_CoverTelemetrySurface()
    {
        Assert.Equal(DiagnosticEventCategories.Exception, "Exception");
        Assert.Equal(DiagnosticEventCategories.DatabaseLatency, "DatabaseLatency");
        Assert.Equal(DiagnosticEventCategories.WorkerHeartbeat, "WorkerHeartbeat");
        Assert.Equal(DiagnosticEventCategories.MraConnectivity, "MraConnectivity");
        Assert.Equal(DiagnosticEventCategories.HealthCheck, "HealthCheck");
    }
}
