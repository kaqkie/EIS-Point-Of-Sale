using System.Text;

namespace PointOfSale.Mra.Billing;

/// <summary>
/// MRA EIS invoice number generator per
/// <c>https://dev-eis-api.mra.mw/docs/invoice_number_generation.htm</c>.
/// </summary>
public static class MraInvoiceNumberGenerator
{
    private const string Base64Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    /// <summary>Historical sandbox seed TIN digits used only in developer/trial builds.</summary>
    public const long SandboxPlaceholderTaxpayerId = 1234567890;

    /// <summary>
    /// Returns true when <paramref name="invoiceNumber"/> matches the MRA composite structure:
    /// Base64(TaxpayerID)-Base64(TerminalPosition)-Base64(JulianDate)-Base64(TransactionCount).
    /// </summary>
    public static bool IsMraCompositeInvoiceNumber(string? invoiceNumber)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return false;
        }

        var trimmed = invoiceNumber.Trim();
        var parts = trimmed.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
            {
                return false;
            }

            foreach (var c in part)
            {
                if (!Base64Chars.Contains(c))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Builds <c>Base64(TaxpayerID)-Base64(TerminalPosition)-Base64(JulianDate)-Base64(Count)</c>
    /// using MRA Base10→Base64 encoding (not UTF-8 string Base64).
    /// </summary>
    public static string Generate(long taxpayerId, int terminalPosition, DateTime transactionDateUtc, long transactionCount)
    {
        if (taxpayerId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(taxpayerId), "Taxpayer ID must be a positive number.");
        }

        if (terminalPosition <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalPosition), "Terminal position must be positive.");
        }

        if (transactionCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transactionCount),
                "Transaction count must start at 1 for MRA daily invoice sequencing.");
        }

        var julianDate = ToJulianDate(transactionDateUtc);
        return string.Join(
            '-',
            Base10ToBase64(taxpayerId),
            Base10ToBase64(terminalPosition),
            Base10ToBase64(julianDate),
            Base10ToBase64(transactionCount));
    }

    /// <summary>MRA Julian day algorithm from the official developers guide.</summary>
    public static int ToJulianDate(DateTime date)
    {
        date = date.Date;
        var year = date.Year;
        var month = date.Month;
        var day = date.Day;

        if (month <= 2)
        {
            year -= 1;
            month += 12;
        }

        var century = year / 100;
        var b = 2 - century + (century / 4);
        return (int)(Math.Floor(365.25 * (year + 4716))
                     + Math.Floor(30.6001 * (month + 1))
                     + day
                     + b
                     - 1524);
    }

    /// <summary>Converts a Base10 integer into MRA compact Base64 (0 → <c>A</c>).</summary>
    public static string Base10ToBase64(long number)
    {
        if (number == 0)
        {
            return "A";
        }

        var result = new StringBuilder();
        while (number > 0)
        {
            var remainder = (int)(number % 64);
            result.Insert(0, Base64Chars[remainder]);
            number /= 64;
        }

        return result.ToString();
    }

    /// <summary>Decodes an MRA compact Base64 segment back to Base10.</summary>
    public static bool TryBase64ToBase10(string? segment, out long number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(segment))
        {
            return false;
        }

        long value = 0;
        foreach (var c in segment.Trim())
        {
            var index = Base64Chars.IndexOf(c);
            if (index < 0)
            {
                return false;
            }

            value = checked(value * 64 + index);
        }

        number = value;
        return true;
    }

    /// <summary>
    /// Reads the TaxpayerID encoded in the first segment of a composite invoice number.
    /// </summary>
    public static bool TryGetEncodedTaxpayerId(string? invoiceNumber, out long taxpayerId)
    {
        taxpayerId = 0;
        if (!TryParseComposite(invoiceNumber, out var parts))
        {
            return false;
        }

        taxpayerId = parts.TaxpayerId;
        return taxpayerId > 0;
    }

    /// <summary>
    /// Parses all four Base64 segments of an MRA composite invoice number.
    /// </summary>
    public static bool TryParseComposite(
        string? invoiceNumber,
        out (long TaxpayerId, int TerminalPosition, int JulianDate, long TransactionCount) parts)
    {
        parts = default;
        if (!IsMraCompositeInvoiceNumber(invoiceNumber))
        {
            return false;
        }

        var segments = invoiceNumber!.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (!TryBase64ToBase10(segments[0], out var tin) || tin <= 0
            || !TryBase64ToBase10(segments[1], out var terminal) || terminal <= 0 || terminal > int.MaxValue
            || !TryBase64ToBase10(segments[2], out var julian) || julian <= 0 || julian > int.MaxValue
            || !TryBase64ToBase10(segments[3], out var count) || count < 0)
        {
            return false;
        }

        parts = (tin, (int)terminal, (int)julian, count);
        return true;
    }

    /// <summary>
    /// True when the invoice number is missing, legacy ART format, non-composite,
    /// encodes the sandbox placeholder TIN, or encodes a different taxpayer id than <paramref name="sellerTin"/>.
    /// </summary>
    public static bool NeedsInvoiceNumberRewrite(string? invoiceNumber, string? sellerTin)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return true;
        }

        var trimmed = invoiceNumber.Trim();
        if (trimmed.StartsWith("ART-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IsMraCompositeInvoiceNumber(trimmed))
        {
            return true;
        }

        if (!TryGetEncodedTaxpayerId(trimmed, out var encodedTin))
        {
            return true;
        }

        // Early sandbox receipts used 1234567890 (Base64 BJlgLS) — rewrite once a real TIN is available.
        if (encodedTin == SandboxPlaceholderTaxpayerId)
        {
            return TryParseTaxpayerId(sellerTin, out var realTin)
                && realTin != SandboxPlaceholderTaxpayerId;
        }

        if (!TryParseTaxpayerId(sellerTin, out var expectedTin)
            || expectedTin == SandboxPlaceholderTaxpayerId)
        {
            return false;
        }

        return encodedTin != expectedTin;
    }

    /// <summary>True when <paramref name="taxpayerId"/> is the historical sandbox seed.</summary>
    public static bool IsSandboxPlaceholderTaxpayerId(long taxpayerId) =>
        taxpayerId == SandboxPlaceholderTaxpayerId;

    /// <summary>Extracts numeric taxpayer id digits from a TIN string.</summary>
    public static bool TryParseTaxpayerId(string? tin, out long taxpayerId)
    {
        taxpayerId = 0;
        if (string.IsNullOrWhiteSpace(tin))
        {
            return false;
        }

        var digits = new string(tin.Where(char.IsDigit).ToArray());
        return digits.Length > 0 && long.TryParse(digits, out taxpayerId) && taxpayerId > 0;
    }
}
