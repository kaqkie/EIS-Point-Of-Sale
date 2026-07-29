using System.Security.Cryptography;
using System.Text;

namespace PointOfSale.Mra.Security;

/// <summary>
/// MRA EIS HMAC-SHA512 helpers per developers guide (UTF-8 input → Base64 digest).
/// Terminal activation confirmation signs the TAC with the shared secret and sends it as <c>x-signature</c>.
/// </summary>
public static class HmacSignatureService
{
    public const string SignatureHeaderName = "x-signature";

    /// <summary>
    /// Computes HMAC-SHA512 over UTF-8 <paramref name="plainText"/> using UTF-8 <paramref name="secretKey"/>
    /// and returns the digest as a Base64 string (MRA EIS <c>x-signature</c> / <c>x-eis-message-hash</c> format).
    /// Empty <paramref name="plainText"/> is allowed (empty request bodies).
    /// </summary>
    public static string ComputeHmacSha512(string plainText, string secretKey)
    {
        ArgumentNullException.ThrowIfNull(plainText);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);

        var digest = ComputeHmacSha512Digest(plainText, secretKey);
        return Convert.ToBase64String(digest);
    }

    /// <summary>
    /// Raw HMAC-SHA512 digest bytes (UTF-8 key and message). Prefer <see cref="ComputeHmacSha512"/> for EIS headers.
    /// Empty <paramref name="plainText"/> is allowed.
    /// </summary>
    public static byte[] ComputeHmacSha512Digest(string plainText, string secretKey)
    {
        ArgumentNullException.ThrowIfNull(plainText);
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
    /// Alias for <see cref="ComputeHmacSha512"/> — kept for existing call sites.
    /// </summary>
    public static string ComputeHmacSha512Base64(string plainText, string secretKey) =>
        ComputeHmacSha512(plainText, secretKey);

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
        return ComputeHmacSha512(terminalActivationCode.Trim(), secretKey);
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
    /// Builds the Base64 HMAC via <see cref="ComputeHmacSha512"/> and attaches <c>x-signature</c>
    /// for terminal activation confirmation.
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
