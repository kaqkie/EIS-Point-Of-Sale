using System.Text;
using System.Text.Json;

namespace PointOfSale.Mra.Security;

/// <summary>
/// Lightweight JWT payload reader for MRA EIS activation tokens.
/// Official samples embed <c>https://mra.mw/TIN</c> and <c>https://mra.mw/DeviceId</c> claims.
/// </summary>
public static class MraJwtClaims
{
    public const string TinClaim = "https://mra.mw/TIN";
    public const string DeviceIdClaim = "https://mra.mw/DeviceId";
    public const string SecretKeyClaim = "https://mra.mw/SecretKey";

    public static string? TryGetTaxpayerTin(string? jwtToken) =>
        TryGetStringClaim(jwtToken, TinClaim);

    public static string? TryGetDeviceId(string? jwtToken) =>
        TryGetStringClaim(jwtToken, DeviceIdClaim);

    public static string? TryGetStringClaim(string? jwtToken, string claimName)
    {
        if (string.IsNullOrWhiteSpace(jwtToken) || string.IsNullOrWhiteSpace(claimName))
        {
            return null;
        }

        if (!TryReadPayload(jwtToken, out var payloadJson) || string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.TryGetProperty(claimName, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// MRA docs send the raw JWT in <c>Authorization</c> (no Bearer prefix).
    /// Strip a leading Bearer so both stored forms work.
    /// </summary>
    public static string NormalizeAuthorizationToken(string? jwtToken)
    {
        if (string.IsNullOrWhiteSpace(jwtToken))
        {
            return string.Empty;
        }

        var trimmed = jwtToken.Trim();
        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed["Bearer ".Length..].Trim();
        }

        return trimmed;
    }

    private static bool TryReadPayload(string jwtToken, out string? payloadJson)
    {
        payloadJson = null;
        var token = NormalizeAuthorizationToken(jwtToken);
        var parts = token.Split('.', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        try
        {
            var payloadBytes = Base64UrlDecode(parts[1]);
            payloadJson = Encoding.UTF8.GetString(payloadBytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }
}
