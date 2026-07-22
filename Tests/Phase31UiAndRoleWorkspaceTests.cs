using PointOfSale.App.Services;
using PointOfSale.Core.Pricing;
using PointOfSale.Core.Security;
using Xunit;

namespace PointOfSale.Tests;

public sealed class Phase31UiAndRoleWorkspaceTests
{
    [Theory]
    [InlineData(OperatorRoles.Cashier, OperatorWorkspace.CashierShell)]
    [InlineData(OperatorRoles.Supervisor, OperatorWorkspace.CashierShell)]
    [InlineData(OperatorRoles.StoreManager, OperatorWorkspace.AdminShell)]
    [InlineData(OperatorRoles.Administrator, OperatorWorkspace.AdminShell)]
    public void OperatorWorkspace_RoutesRolesToExpectedShell(string role, string expectedShell)
    {
        Assert.Equal(expectedShell, OperatorWorkspace.ResolveShell(role));
    }

    [Fact]
    public void PosTaxCalculator_StatutoryVatRemainsSeventeenPointFive()
    {
        Assert.Equal(17.5m, PosTaxCalculator.MalawiStandardVatRatePercent);
        var vat = PosTaxCalculator.CalculateVatAmount(1000m, PosTaxCalculator.MalawiStandardVatRatePercent);
        Assert.Equal(175.00m, vat);
    }

    [Fact]
    public void RolePermissionCatalog_CashierCannotManageUsers()
    {
        var cashier = RolePermissionCatalog.GetPermissions(OperatorRoles.Cashier);
        Assert.Contains(OperatorPermissions.ExecuteCheckout, cashier);
        Assert.DoesNotContain(OperatorPermissions.ManageUsers, cashier);
        Assert.DoesNotContain(OperatorPermissions.AccessAdminAnalytics, cashier);
    }

    [Fact]
    public void RolePermissionCatalog_AdministratorHasFullDirectory()
    {
        var admin = RolePermissionCatalog.GetPermissions(OperatorRoles.Administrator);
        Assert.Contains(OperatorPermissions.ManageUsers, admin);
        Assert.Contains(OperatorPermissions.AccessAdminAnalytics, admin);
        Assert.Contains(OperatorPermissions.ManageHardwarePeripherals, admin);
    }
}
