using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using PointOfSale.App.Options;
using PointOfSale.App.Services;
using PointOfSale.Core.Entities;
using PointOfSale.Core.Security;
using Xunit;

namespace PointOfSale.Tests;

public sealed class DatabaseMaintenanceTests
{
    [Fact]
    public void RolePermissionCatalog_StoreManagerCanManageDatabaseMaintenance()
    {
        var permissions = RolePermissionCatalog.GetPermissions(OperatorRoles.StoreManager);
        Assert.Contains(OperatorPermissions.ManageDatabaseMaintenance, permissions);
        Assert.DoesNotContain(
            OperatorPermissions.ManageDatabaseMaintenance,
            RolePermissionCatalog.GetPermissions(OperatorRoles.Cashier));
    }

    [Fact]
    public void DatabaseMaintenanceOptions_DefaultsAreProductionSafe()
    {
        var options = new DatabaseMaintenanceOptions();
        Assert.True(options.Enabled);
        Assert.False(options.AllowRebuildDuringOpenShift);
        Assert.True(options.RebuildFragmentationPercentThreshold >= 10);
    }

    [Fact]
    public async Task DatabaseMaintenanceService_BlocksRebuildWhenShiftOpen()
    {
        var shifts = new Mock<IShiftManagementService>();
        shifts.Setup(s => s.GetOpenShiftAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashierShift
            {
                ShiftId = 3,
                CashierName = "Jane",
                Status = ShiftStatuses.Open,
                OpenedAtUtc = DateTime.UtcNow
            });

        var auth = new Mock<IAuthenticationAuthorizationService>();
        auth.Setup(a => a.HasPermission(It.IsAny<string>())).Returns(true);

        var service = CreateService(shifts.Object, auth.Object);

        var result = await service.RunMaintenanceAsync(DatabaseMaintenanceOperations.RebuildIndexes);
        Assert.False(result.Success);
        Assert.Contains("shift", result.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static DatabaseMaintenanceService CreateService(
        IShiftManagementService shifts,
        IAuthenticationAuthorizationService auth) =>
        new(
            Mock.Of<PointOfSale.Infrastructure.Data.ISqlConnectionFactory>(),
            shifts,
            auth,
            Mock.Of<ITelemetryDiagnosticService>(),
            Options.Create(new DatabaseMaintenanceOptions()),
            Options.Create(new SystemDiagnosticsOptions()),
            NullLogger<DatabaseMaintenanceService>.Instance);
}
