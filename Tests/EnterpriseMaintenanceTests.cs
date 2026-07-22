using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using Xunit;

namespace PointOfSale.Tests;

public sealed class EnterpriseMaintenanceTests
{
    [Fact]
    public void RolePermissionCatalog_StoreManagerCanExecuteEnterpriseMaintenance()
    {
        var permissions = RolePermissionCatalog.GetPermissions(OperatorRoles.StoreManager);
        Assert.Contains(OperatorPermissions.ExecuteEnterpriseMaintenance, permissions);
        Assert.DoesNotContain(
            OperatorPermissions.ExecuteEnterpriseMaintenance,
            RolePermissionCatalog.GetPermissions(OperatorRoles.Cashier));
    }

    [Fact]
    public void EnterprisePerformanceOptions_DefaultsEnableProfiling()
    {
        var options = new EnterprisePerformanceOptions();
        Assert.True(options.Enabled);
        Assert.True(options.ProfilingIntervalSeconds >= 10);
        Assert.True(options.TelemetryFlushIntervalSeconds >= 60);
    }

    [Fact]
    public async Task EnterpriseMaintenanceService_BlocksIndexMaintenanceWhenShiftOpen()
    {
        var shifts = new Mock<IShiftManagementService>();
        shifts.Setup(s => s.GetOpenShiftAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashierShift
            {
                ShiftId = 7,
                CashierName = "Bob",
                Status = ShiftStatuses.Open,
                OpenedAtUtc = DateTime.UtcNow
            });

        var auth = new Mock<IAuthenticationAuthorizationService>();
        auth.Setup(a => a.HasPermission(It.IsAny<string>())).Returns(true);

        var service = new EnterpriseMaintenanceService(
            shifts.Object,
            Mock.Of<IPerformanceProfilingService>(),
            Mock.Of<ITelemetryDiagnosticService>(),
            null!,
            Mock.Of<PointOfSale.Infrastructure.Data.ISqlConnectionFactory>(),
            auth.Object,
            Options.Create(new EnterpriseMaintenanceOptions()),
            NullLogger<EnterpriseMaintenanceService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ExecuteCommandAsync(EnterpriseMaintenanceCommandTypes.ReorganizeIndexes));
    }

    [Fact]
    public async Task EnterpriseMaintenanceService_ClearCachesSucceedsWithoutOpenShiftCheck()
    {
        var shifts = new Mock<IShiftManagementService>();
        shifts.Setup(s => s.GetOpenShiftAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((CashierShift?)null);

        var profiling = new Mock<IPerformanceProfilingService>();
        profiling.Setup(p => p.CaptureSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PerformanceProfileSnapshot());

        var telemetry = new Mock<ITelemetryDiagnosticService>();
        telemetry.Setup(t => t.PurgeExpiredAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var auth = new Mock<IAuthenticationAuthorizationService>();
        auth.Setup(a => a.HasPermission(It.IsAny<string>())).Returns(true);

        var service = new EnterpriseMaintenanceService(
            shifts.Object,
            profiling.Object,
            telemetry.Object,
            null!,
            Mock.Of<PointOfSale.Infrastructure.Data.ISqlConnectionFactory>(),
            auth.Object,
            Options.Create(new EnterpriseMaintenanceOptions()),
            NullLogger<EnterpriseMaintenanceService>.Instance);

        var result = await service.ExecuteCommandAsync(EnterpriseMaintenanceCommandTypes.ClearCaches);
        Assert.True(result.Success);
        Assert.Equal(EnterpriseMaintenanceCommandTypes.ClearCaches, result.CommandType);
    }
}
