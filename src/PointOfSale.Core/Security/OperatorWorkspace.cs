namespace PointOfSale.Core.Security;

/// <summary>
/// Maps operator roles to Phase 31 workspace shells (Cashier counter vs Admin console).
/// </summary>
public static class OperatorWorkspace
{
    public const string CashierShell = "Cashier";
    public const string AdminShell = "Admin";

    /// <summary>
    /// Store managers and administrators land on the Admin Management console;
    /// cashiers and supervisors use the POS counter workspace.
    /// </summary>
    public static string ResolveShell(string? role) =>
        IsAdminConsoleRole(role) ? AdminShell : CashierShell;

    public static bool IsAdminConsoleRole(string? role) =>
        string.Equals(role, OperatorRoles.StoreManager, StringComparison.OrdinalIgnoreCase)
        || string.Equals(role, OperatorRoles.Administrator, StringComparison.OrdinalIgnoreCase);

    public static bool IsCashierWorkspaceRole(string? role) => !IsAdminConsoleRole(role);
}
