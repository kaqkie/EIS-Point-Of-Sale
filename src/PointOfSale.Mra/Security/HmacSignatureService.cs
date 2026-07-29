using System.Security.Cryptography;
using System.Text;

namespace PointOfSale.Mra.Security;

/// <summary>
/// MRA EIS HMAC-SHA512 helpers per developers guide (Base64-encoded digest).
/// Terminal activation confirmation signs the TAC with the shared secret and sends it as <c>x-signature</c>.
/// </summary>
public static class HmacSignatureService
{
    public const string SignatureHeaderName = "x-signature";

    /// <summary>
    /// Computes raw HMAC-SHA512 bytes over UTF-8 <paramref name="plainText"/> using <paramref name="secretKey"/>.
    /// </summary>
    public static byte[] ComputeHmacSha512(string plainText, string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainText);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);

        var keyBytes = Encoding.UTF8.GetBytes(secretKey);
        var dataBytes = Encoding.UTF8.GetBytes(plainText);
        return HMACSHA512.HashData(keyBytes, dataBytes);
    }

    /// <summary>
    /// Converts an HMAC digest to the Base64 string required by the MRA EIS <c>x-signature</c> header.
    /// </summary>
    public static string EncodeSignatureBase64(byte[] hmacSha512Digest)
    {
        ArgumentNullException.ThrowIfNull(hmacSha512Digest);
        if (hmacSha512Digest.Length == 0)
        {
            throw new ArgumentException("HMAC digest cannot be empty.", nameof(hmacSha512Digest));
        }

        return Convert.ToBase64String(hmacSha512Digest);
    }

    /// <summary>
    /// Standard EIS signature: HMAC-SHA512(plainText, secretKey) → Base64.
    /// Used for request payloads on sales and most signed endpoints.
    /// </summary>
    public static string ComputeHmacSha512Base64(string plainText, string secretKey) =>
        EncodeSignatureBase64(ComputeHmacSha512(plainText, secretKey));

    /// <summary>
    /// Terminal activated confirmation: HMAC-SHA512(TAC, secretKey) → Base64.
    /// Per MRA, the confirmation endpoint signs the Terminal Activation Code (not the JSON body).
    /// </summary>
    public static string ComputeActivationConfirmationSignature(
        string terminalActivationCode,
        string secretKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(terminalActivationCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);
        return ComputeHmacSha512Base64(terminalActivationCode.Trim(), secretKey);
    }

    /// <summary>
    /// Injects the mandatory <c>x-signature</c> header used exclusively for signed MRA calls
    /// (including <c>onboarding/terminal-activated-confirmation</c>).
    /// </summary>
    public static void ApplyXSignatureHeader(HttpRequestMessage request, string base64Signature)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Signature);

        // Replace any prior value so confirmation retries cannot send a stale signature.
        request.Headers.Remove(SignatureHeaderName);
        if (!request.Headers.TryAddWithoutValidation(SignatureHeaderName, base64Signature.Trim()))
        {
            throw new InvalidOperationException(
                $"Failed to attach required {SignatureHeaderName} header for MRA EIS request.");
        }
    }

    /// <summary>
    /// Builds the Base64 HMAC and attaches <c>x-signature</c> for terminal activation confirmation.
    /// </summary>
    public static string AttachActivationConfirmationSignature(
        HttpRequestMessage request,
        string terminalActivationCode,
        string secretKey)
    {
        var signature = ComputeActivationConfirmationSignature(terminalActivationCode, secretKey);
        ApplyXSignatureHeader(request, signature);
        return signature;
    }
}
