using PointOfSale.App.Options;
using PointOfSale.App.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class Phase36SystemHealthMonitorTests
{
    [Fact]
    public void SystemHealthMonitorSnapshot_ExposesPhase36DashboardFields()
    {
        var snapshot = new SystemHealthMonitorSnapshot
        {
            IsDatabaseHealthy = true,
            IsInternetOnline = true,
            IsMraApiReachable = true,
            DiskSpaceFreeMb = 2048.5,
            ActiveSyncQueueCount = 3,
            DatabaseLatencyMs = 42,
            OverallHealthy = true,
            Summary = "All monitored subsystems healthy."
        };

        Assert.True(snapshot.IsDatabaseHealthy);
        Assert.True(snapshot.IsInternetOnline);
        Assert.True(snapshot.IsMraApiReachable);
        Assert.Equal(2048.5, snapshot.DiskSpaceFreeMb);
        Assert.Equal(3, snapshot.ActiveSyncQueueCount);
    }

    [Fact]
    public void SystemHealthAlertCodes_CoverCoreFaults()
    {
        Assert.Equal("DATABASE_UNHEALTHY", SystemHealthAlertCodes.DatabaseUnhealthy);
        Assert.Equal("DISK_LOW", SystemHealthAlertCodes.DiskLow);
        Assert.Equal("INTERNET_OFFLINE", SystemHealthAlertCodes.InternetOffline);
        Assert.Equal("MRA_UNREACHABLE", SystemHealthAlertCodes.MraUnreachable);
        Assert.Equal("QUEUE_BACKLOG", SystemHealthAlertCodes.QueueBacklog);
    }

    [Fact]
    public void SystemDiagnosticsOptions_Phase36ThresholdDefaults()
    {
        var options = new SystemDiagnosticsOptions();
        Assert.Equal(25, options.QueueBacklogWarnCount);
        Assert.Equal(120, options.HealthAlertCooldownSeconds);
        Assert.Equal(500, options.MinimumFreeDiskMegabytes);
        Assert.Equal(1500, options.DatabaseLatencyWarnMs);
    }

    [Fact]
    public void SystemHealthAlert_DefaultsSeverityToWarning()
    {
        var alert = new SystemHealthAlert
        {
            Code = SystemHealthAlertCodes.QueueBacklog,
            Message = "backlog"
        };
        Assert.Equal(DiagnosticSeverities.Warning, alert.Severity);
        Assert.True(alert.RaisedAtUtc <= DateTime.UtcNow.AddSeconds(1));
    }
}
