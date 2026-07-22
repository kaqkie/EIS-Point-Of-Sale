using System.Text;
using System.Text.RegularExpressions;

namespace PointOfSale.App.Services;

/// <summary>
/// Phase 41 — masked license key input: uppercase, automatic hyphenation, and exact
/// <c>XXXX-XXXX-XXXX-XXXX</c> regex validation (<c>^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$</c>).
/// </summary>
public static partial class LicenseKeyInputFormatter
{
    public const int MaxLength = 19;
    public const int AlphanumericLength = 16;
    public const string ExactFormatPattern = @"^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$";
    public const string Placeholder = "XXXX-XXXX-XXXX-XXXX";
    public const string FormatErrorMessage =
        "Activation key must match XXXX-XXXX-XXXX-XXXX (A–Z / 0–9), e.g. I4CV-M5YY-AKY6-Z9BT.";
    public const string IncompleteHintMessage =
        "Keep typing — letters/digits only; hyphens are inserted automatically.";

    /// <summary>
    /// Strips non-alphanumerics, uppercases, and inserts hyphens after every 4 characters
    /// (max 16 alphanumerics → 19 including hyphens).
    /// </summary>
    public static string ApplyMask(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[AlphanumericLength];
        var count = 0;
        foreach (var ch in raw)
        {
            var upper = char.ToUpperInvariant(ch);
            if (upper is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                buffer[count++] = upper;
                if (count == AlphanumericLength)
                {
                    break;
                }
            }
        }

        if (count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(MaxLength);
        for (var i = 0; i < count; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                sb.Append('-');
            }

            sb.Append(buffer[i]);
        }

        return sb.ToString();
    }

    public static bool IsExactFormat(string? value) =>
        !string.IsNullOrEmpty(value) && ExactFormatRegex().IsMatch(value);

    /// <summary>True while the user is mid-entry with a structurally valid partial key.</summary>
    public static bool IsValidPartial(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        return PartialFormatRegex().IsMatch(value);
    }

    public static bool ShouldShowFormatError(string? value)
    {
        if (string.IsNullOrEmpty(value) || IsExactFormat(value))
        {
            return false;
        }

        // Full-length non-match, or any structurally broken partial (e.g. paste edge cases).
        return value.Length >= MaxLength || !IsValidPartial(value);
    }

    public static bool IsIncomplete(string? value) =>
        !string.IsNullOrEmpty(value) && !IsExactFormat(value) && IsValidPartial(value);

    public static string? GetLiveFeedbackMessage(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (IsExactFormat(value))
        {
            return null;
        }

        if (ShouldShowFormatError(value))
        {
            return FormatErrorMessage;
        }

        return IncompleteHintMessage;
    }

    [GeneratedRegex(ExactFormatPattern, RegexOptions.CultureInvariant)]
    private static partial Regex ExactFormatRegex();

    /// <summary>
    /// Progressive schema while typing: each group is 0–4 alphanumerics, hyphens only between groups.
    /// </summary>
    [GeneratedRegex(
        @"^([A-Z0-9]{0,4}|[A-Z0-9]{4}-[A-Z0-9]{0,4}|[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{0,4}|[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{0,4})$",
        RegexOptions.CultureInvariant)]
    private static partial Regex PartialFormatRegex();
}
