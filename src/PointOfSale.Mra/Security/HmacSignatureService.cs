using System.Security.Cryptography;
using System.Text;

namespace PointOfSale.Mra.Security;

/// <summary>
/// MRA EIS HMAC-SHA512 helpers per developers guide (Base64-encoded digest).
/// </summary>
public static class HmacSignatureService
{
    public const string SignatureHeaderName = "x-signature";

    /// <summary>
    /// Standard EIS signature: HMAC-SHA512(plainText, secretKey) → Base64.
    /// Used for request payloads on sales and most signed endpoints.
    /// </summary>
    public static string ComputeHmacSha512Base64(string plainText, string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainText);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);

        var keyBytes = Encoding.UTF8.GetBytes(secretKey);
        var dataBytes = Encoding.UTF8.GetBytes(plainText);

        Span<byte> hash = stackalloc byte[HMACSHA512.HashSizeInBytes];
        HMACSHA512.HashData(keyBytes, dataBytes, hash);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Terminal activated confirmation: HMAC-SHA512(TAC, secretKey) → Base64.
    /// </summary>
    public static string ComputeActivationConfirmationSignature(
        string terminalActivationCode,
        string secretKey) =>
        ComputeHmacSha512Base64(terminalActivationCode, secretKey);
}
