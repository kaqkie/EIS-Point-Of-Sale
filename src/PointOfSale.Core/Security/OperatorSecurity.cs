namespace PointOfSale.Core.Security;

public static class OperatorRoles
{
    public const string Cashier = "Cashier";
    public const string Supervisor = "Supervisor";
    public const string StoreManager = "StoreManager";
    public const string Administrator = "Administrator";

    public static readonly string[] All =
    [
        Cashier,
        Supervisor,
        StoreManager,
        Administrator
    ];
}

public static class OperatorPermissions
{
    public const string ExecuteCheckout = "ExecuteCheckout";
    public const string OverridePrice = "OverridePrice";
    public const string PerformVoid = "PerformVoid";
    public const string AccessAdminAnalytics = "AccessAdminAnalytics";
    public const string TriggerBackup = "TriggerBackup";
    public const string ManageQueueSync = "ManageQueueSync";
    public const string ManageInventory = "ManageInventory";
    public const string AccessCompliance = "AccessCompliance";
    public const string AccessHeadOffice = "AccessHeadOffice";
    public const string ManageUsers = "ManageUsers";
    public const string OpenCashDrawer = "OpenCashDrawer";
    public const string LookupLoyaltyCustomer = "LookupLoyaltyCustomer";
    public const string RedeemLoyaltyPoints = "RedeemLoyaltyPoints";
    public const string ManageLoyaltyPrograms = "ManageLoyaltyPrograms";
    public const string ApplyCartDiscount = "ApplyCartDiscount";
    public const string PrintProductLabels = "PrintProductLabels";
    public const string ManageLabelBatches = "ManageLabelBatches";
    public const string ViewInventoryAlerts = "ViewInventoryAlerts";
    public const string ManagePurchaseOrders = "ManagePurchaseOrders";
    public const string ProcessGoodsReceipt = "ProcessGoodsReceipt";
    public const string ReconcileSupplierInvoices = "ReconcileSupplierInvoices";
    public const string ViewSystemDiagnostics = "ViewSystemDiagnostics";
    public const string CloseFinancialDay = "CloseFinancialDay";
    public const string ExecuteFiscalYearRollover = "ExecuteFiscalYearRollover";
    public const string ProvisionTerminal = "ProvisionTerminal";
    public const string RunIntegrationTests = "RunIntegrationTests";
    public const string ExecuteEnterpriseMaintenance = "ExecuteEnterpriseMaintenance";

    public static readonly string[] All =
    [
        ExecuteCheckout,
        OverridePrice,
        PerformVoid,
        AccessAdminAnalytics,
        TriggerBackup,
        ManageQueueSync,
        ManageInventory,
        AccessCompliance,
        AccessHeadOffice,
        ManageUsers,
        OpenCashDrawer,
        LookupLoyaltyCustomer,
        RedeemLoyaltyPoints,
        ManageLoyaltyPrograms,
        ApplyCartDiscount,
        PrintProductLabels,
        ManageLabelBatches,
        ViewInventoryAlerts,
        ManagePurchaseOrders,
        ProcessGoodsReceipt,
        ReconcileSupplierInvoices,
        ViewSystemDiagnostics,
        CloseFinancialDay,
        ExecuteFiscalYearRollover,
        ProvisionTerminal,
        RunIntegrationTests,
        ExecuteEnterpriseMaintenance
    ];
}

public sealed class OperatorAccount
{
    public int OperatorId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = OperatorRoles.Cashier;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public int PasswordIterations { get; set; } = 100_000;
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutUntilUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
}

public sealed class OperatorSession
{
    public int OperatorId { get; init; }
    public string Username { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public IReadOnlySet<string> Permissions { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public DateTime SignedInAtUtc { get; init; }
}
