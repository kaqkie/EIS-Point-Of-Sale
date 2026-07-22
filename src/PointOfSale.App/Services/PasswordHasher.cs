using System.Security.Cryptography;
using System.Text;
using PointOfSale.Core.Security;

namespace PointOfSale.App.Services;

public interface IPasswordHasher
{
    (string HashBase64, string SaltBase64, int Iterations) HashPassword(string password);
    bool VerifyPassword(string password, string hashBase64, string saltBase64, int iterations);
}

/// <summary>PBKDF2-SHA256 password hashing for operator credentials.</summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    public const int DefaultIterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public (string HashBase64, string SaltBase64, int Iterations) HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            DefaultIterations,
            HashAlgorithmName.SHA256,
            KeySize);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), DefaultIterations);
    }

    public bool VerifyPassword(string password, string hashBase64, string saltBase64, int iterations)
    {
        if (string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(hashBase64) ||
            string.IsNullOrWhiteSpace(saltBase64) ||
            iterations < 10_000)
        {
            return false;
        }

        byte[] expected;
        byte[] salt;
        try
        {
            expected = Convert.FromBase64String(hashBase64);
            salt = Convert.FromBase64String(saltBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

public static class RolePermissionCatalog
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Map =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [OperatorRoles.Cashier] = new HashSet<string>(StringComparer.Ordinal)
            {
                OperatorPermissions.ExecuteCheckout,
                OperatorPermissions.ManageQueueSync,
                OperatorPermissions.OpenCashDrawer,
                OperatorPermissions.LookupLoyaltyCustomer,
                OperatorPermissions.RedeemLoyaltyPoints
            },
            [OperatorRoles.Supervisor] = new HashSet<string>(StringComparer.Ordinal)
            {
                OperatorPermissions.ExecuteCheckout,
                OperatorPermissions.OverridePrice,
                OperatorPermissions.PerformVoid,
                OperatorPermissions.ManageQueueSync,
                OperatorPermissions.ManageInventory,
                OperatorPermissions.OpenCashDrawer,
                OperatorPermissions.LookupLoyaltyCustomer,
                OperatorPermissions.RedeemLoyaltyPoints,
                OperatorPermissions.ApplyCartDiscount,
                OperatorPermissions.PrintProductLabels,
                OperatorPermissions.ViewInventoryAlerts,
                OperatorPermissions.ProcessGoodsReceipt
            },
            [OperatorRoles.StoreManager] = new HashSet<string>(StringComparer.Ordinal)
            {
                OperatorPermissions.ExecuteCheckout,
                OperatorPermissions.OverridePrice,
                OperatorPermissions.PerformVoid,
                OperatorPermissions.AccessAdminAnalytics,
                OperatorPermissions.TriggerBackup,
                OperatorPermissions.ManageQueueSync,
                OperatorPermissions.ManageInventory,
                OperatorPermissions.AccessCompliance,
                OperatorPermissions.AccessHeadOffice,
                OperatorPermissions.OpenCashDrawer,
                OperatorPermissions.LookupLoyaltyCustomer,
                OperatorPermissions.RedeemLoyaltyPoints,
                OperatorPermissions.ManageLoyaltyPrograms,
                OperatorPermissions.ApplyCartDiscount,
                OperatorPermissions.PrintProductLabels,
                OperatorPermissions.ManageLabelBatches,
                OperatorPermissions.ViewInventoryAlerts,
                OperatorPermissions.ManagePurchaseOrders,
                OperatorPermissions.ProcessGoodsReceipt,
                OperatorPermissions.ReconcileSupplierInvoices,
                OperatorPermissions.ViewSystemDiagnostics,
                OperatorPermissions.CloseFinancialDay,
                OperatorPermissions.ExecuteFiscalYearRollover,
                OperatorPermissions.ProvisionTerminal,
                OperatorPermissions.RunIntegrationTests,
                OperatorPermissions.ExecuteEnterpriseMaintenance
            },
            [OperatorRoles.Administrator] = new HashSet<string>(OperatorPermissions.All, StringComparer.Ordinal)
        };

    public static IReadOnlySet<string> GetPermissions(string role) =>
        Map.TryGetValue(role, out var set) ? set : Map[OperatorRoles.Cashier];
}
