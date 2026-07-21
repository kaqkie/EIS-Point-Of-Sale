using System.Text.RegularExpressions;

namespace PointOfSale.Infrastructure.Security;

public static partial class SensitiveDataScrubber
{
    private const string Redacted = "***REDACTED***";

    public static string Scrub(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var scrubbed = value;
        scrubbed = JwtTokenPattern().Replace(scrubbed, Redacted);
        scrubbed = SecretKeyJsonPattern().Replace(scrubbed, $"\"secretKey\":\"{Redacted}\"");
        scrubbed = SecretKeyCamelPattern().Replace(scrubbed, $"\"SecretKey\":\"{Redacted}\"");
        scrubbed = JwtTokenJsonPattern().Replace(scrubbed, $"\"jwtToken\":\"{Redacted}\"");
        scrubbed = AuthorizationHeaderPattern().Replace(scrubbed, $"Authorization: {Redacted}");
        scrubbed = XSignatureHeaderPattern().Replace(scrubbed, $"x-signature: {Redacted}");
        scrubbed = ApiKeyJsonPattern().Replace(scrubbed, $"\"apiKey\":\"{Redacted}\"");
        scrubbed = OfflineSignaturePattern().Replace(scrubbed, $"\"offlineSignature\":\"{Redacted}\"");
        return scrubbed;
    }

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+\b", RegexOptions.CultureInvariant)]
    private static partial Regex JwtTokenPattern();

    [GeneratedRegex(@"""secretKey""\s*:\s*""[^""]*""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyJsonPattern();

    [GeneratedRegex(@"""SecretKey""\s*:\s*""[^""]*""", RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeyCamelPattern();

    [GeneratedRegex(@"""jwtToken""\s*:\s*""[^""]*""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex JwtTokenJsonPattern();

    [GeneratedRegex(@"Authorization\s*:\s*[^\r\n""]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AuthorizationHeaderPattern();

    [GeneratedRegex(@"x-signature\s*:\s*[^\r\n""]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex XSignatureHeaderPattern();

    [GeneratedRegex(@"""apiKey""\s*:\s*""[^""]*""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyJsonPattern();

    [GeneratedRegex(@"""offlineSignature""\s*:\s*""[^""]*""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex OfflineSignaturePattern();
}
