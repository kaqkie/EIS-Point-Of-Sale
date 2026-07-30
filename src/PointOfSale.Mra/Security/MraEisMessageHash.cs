namespace PointOfSale.Mra.Security;

    /// <summary>
    /// MRA EIS <c>x-eis-message-hash</c> policy — required on authenticated requests except
    /// onboarding activate/confirm (developers guide §4.1 + confirmation uses <c>x-signature</c>).
    /// </summary>
public static class MraEisMessageHash
{
    public const string HeaderName = "x-eis-message-hash";

    /// <summary>
    /// Optional override for the HMAC plaintext when it differs from the raw HTTP body
    /// (set via <see cref="HttpRequestMessage.Options"/>).
    /// </summary>
    public static readonly HttpRequestOptionsKey<string> PlainTextOptionKey = new("mra.eis.messageHash.plainText");

    /// <summary>
    /// Terminal secret used to compute the message hash (set via <see cref="HttpRequestMessage.Options"/>).
    /// </summary>
    public static readonly HttpRequestOptionsKey<string> SecretKeyOptionKey = new("mra.eis.messageHash.secretKey");

    /// <summary>
    /// True for onboarding routes that must not carry <c>x-eis-message-hash</c>:
    /// <c>activate-terminal</c> (no secret yet) and <c>terminal-activated-confirmation</c>
    /// (auth is JWT + <c>x-signature</c> over the TAC only).
    /// </summary>
    public static bool IsTerminalActivationRequest(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return IsExemptFromMessageHash(request.RequestUri?.ToString());
    }

    public static bool IsTerminalActivationPath(string? pathOrUri) =>
        IsExemptFromMessageHash(pathOrUri);

    public static bool IsExemptFromMessageHash(string? pathOrUri)
    {
        if (string.IsNullOrWhiteSpace(pathOrUri))
        {
            return false;
        }

        return pathOrUri.Contains("activate-terminal", StringComparison.OrdinalIgnoreCase)
            || pathOrUri.Contains("terminal-activated-confirmation", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldAttach(HttpRequestMessage request) =>
        !IsTerminalActivationRequest(request);

    /// <summary>
    /// HMAC-SHA512(payload, secretKey) → Base64 for the <c>x-eis-message-hash</c> header.
    /// </summary>
    public static string Compute(string payloadPlainText, string secretKey) =>
        HmacSignatureService.ComputeHmacSha512(payloadPlainText ?? string.Empty, secretKey);

    public static void ApplyHeader(HttpRequestMessage request, string base64MessageHash)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64MessageHash);

        request.Headers.Remove(HeaderName);
        if (!request.Headers.TryAddWithoutValidation(HeaderName, base64MessageHash.Trim()))
        {
            throw new InvalidOperationException(
                $"Failed to attach required {HeaderName} header for MRA EIS request.");
        }
    }

    /// <summary>
    /// Computes and attaches <c>x-eis-message-hash</c> when the route is not terminal activation.
    /// </summary>
    public static string? TryAttach(HttpRequestMessage request, string payloadPlainText, string secretKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ShouldAttach(request) || string.IsNullOrWhiteSpace(secretKey))
        {
            return null;
        }

        var hash = Compute(payloadPlainText ?? string.Empty, secretKey);
        ApplyHeader(request, hash);
        return hash;
    }

    public static void SetSecretKeyOption(HttpRequestMessage request, string? secretKey)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.IsNullOrWhiteSpace(secretKey))
        {
            request.Options.Set(SecretKeyOptionKey, secretKey);
        }
    }

    public static void SetPlainTextOption(HttpRequestMessage request, string? plainText)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (plainText is not null)
        {
            request.Options.Set(PlainTextOptionKey, plainText);
        }
    }
}
