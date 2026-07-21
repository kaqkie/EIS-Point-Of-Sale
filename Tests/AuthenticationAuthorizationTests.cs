using PointOfSale.App.Services;
using PointOfSale.Core.Security;
using Xunit;

namespace PointOfSale.Tests;

public sealed class AuthenticationAuthorizationTests
{
    [Fact]
    public void Pbkdf2PasswordHasher_RoundTrips()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var (hash, salt, iterations) = hasher.HashPassword("ChangeMe!123");
        Assert.True(iterations >= 100_000);
        Assert.True(hasher.VerifyPassword("ChangeMe!123", hash, salt, iterations));
        Assert.False(hasher.VerifyPassword("wrong-password", hash, salt, iterations));
    }

    [Theory]
    [InlineData(OperatorRoles.Cashier, OperatorPermissions.ExecuteCheckout, true)]
    [InlineData(OperatorRoles.Cashier, OperatorPermissions.TriggerBackup, false)]
    [InlineData(OperatorRoles.StoreManager, OperatorPermissions.AccessAdminAnalytics, true)]
    [InlineData(OperatorRoles.Administrator, OperatorPermissions.ManageUsers, true)]
    [InlineData(OperatorRoles.Supervisor, OperatorPermissions.PerformVoid, true)]
    [InlineData(OperatorRoles.Supervisor, OperatorPermissions.ManageUsers, false)]
    public void RolePermissionCatalog_EnforcesExpectedMatrix(string role, string permission, bool expected)
    {
        var set = RolePermissionCatalog.GetPermissions(role);
        Assert.Equal(expected, set.Contains(permission));
    }

    [Fact]
    public void Administrator_HasAllDeclaredPermissions()
    {
        var set = RolePermissionCatalog.GetPermissions(OperatorRoles.Administrator);
        foreach (var permission in OperatorPermissions.All)
        {
            Assert.Contains(permission, set);
        }
    }
}
