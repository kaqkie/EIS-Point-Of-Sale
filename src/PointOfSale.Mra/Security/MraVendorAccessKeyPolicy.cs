using PointOfSale.Mra.Options;

namespace PointOfSale.Mra.Security;

/// <summary>
/// Production terminal activation requires the vendor <c>x-access-key</c> issued by MRA
/// after system certification. Sandbox activation must not send this header.
/// </summary>
public static class MraVendorAccessKeyPolicy
{
    public const string HeaderName = "x-access-key";

    public static bool IsProductionEnvironment(MraApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Environment.Equals("Production", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the activate-terminal call must include <see cref="HeaderName"/>.
    /// </summary>
    public static bool RequiresVendorAccessKey(MraApiOptions options) =>
        IsProductionEnvironment(options);

    /// <summary>
    /// Returns the trimmed vendor access key for production activation, or <c>null</c> for sandbox.
    /// Throws when production is selected but the key is missing.
    /// </summary>
    public static string? ResolveForActivateTerminal(MraApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!RequiresVendorAccessKey(options))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.VendorAccessKey)
            || options.VendorAccessKey.Contains('{', StringComparison.Ordinal)
            || options.VendorAccessKey.Contains('}', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Production terminal activation requires MraEis:VendorAccessKey (x-access-key) " +
                "issued by MRA after system certification. Set it in appsettings.Production.json " +
                "or a secure deploy secret — do not use the sandbox environment without this key.");
        }

        return options.VendorAccessKey.Trim();
    }

    public static bool TryResolveForActivateTerminal(
        MraApiOptions options,
        out string? accessKey,
        out string? errorMessage)
    {
        accessKey = null;
        errorMessage = null;

        try
        {
            accessKey = ResolveForActivateTerminal(options);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }
}
