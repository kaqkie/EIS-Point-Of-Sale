using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PointOfSale.Infrastructure.Security;

/// <summary>
/// Protects terminal secrets at rest using Windows DPAPI (CurrentUser scope).
/// </summary>
public interface ISecretProtector
{
    string Protect(string plainText);
    string Unprotect(string protectedBase64);
}

[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("PointOfSale.Mra.TerminalSecret.v1");

    public string Protect(string plainText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainText);
        var bytes = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectedData.Protect(bytes, OptionalEntropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedBase64)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedBase64);
        var protectedBytes = Convert.FromBase64String(protectedBase64);
        var bytes = ProtectedData.Unprotect(protectedBytes, OptionalEntropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
