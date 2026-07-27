using System.Text;

namespace PointOfSale.Mra.Billing;

/// <summary>
/// MRA EIS invoice number generator per
/// <c>https://dev-eis-api.mra.mw/docs/invoice_number_generation.htm</c>.
/// </summary>
public static class MraInvoiceNumberGenerator
{
    private const string Base64Chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

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

        if (transactionCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(transactionCount), "Transaction count cannot be negative.");
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
