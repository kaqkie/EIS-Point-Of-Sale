using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using PointOfSale.Mra.Billing;
using PointOfSale.Mra.Contracts.Sales;
using PointOfSale.Mra.Security;
using PointOfSale.Tests.Support;
using Xunit;

namespace PointOfSale.Tests;

public sealed class MraOfflineReceiptSigningTests
{
    private const string SecretKey = "ART-Offline-Test-Secret-Key";

    [Fact]
    public void ToJulianDate_AndBase10ToBase64_MatchInvoiceGenerator()
    {
        var date = new DateTime(2024, 4, 26, 0, 0, 0, DateTimeKind.Utc);
        var julian = MraOfflineReceiptSigning.ToJulianDate(date);
        Assert.Equal(MraInvoiceNumberGenerator.ToJulianDate(date), julian);
        Assert.Equal(
            MraInvoiceNumberGenerator.Base10ToBase64(julian),
            MraOfflineReceiptSigning.Base10ToBase64(julian));
    }

    [Fact]
    public void GenerateCombinedString_MatchesCompositeInvoiceNumber()
    {
        var date = new DateTime(2024, 4, 26, 12, 0, 0, DateTimeKind.Utc);
        var julian = MraOfflineReceiptSigning.ToJulianDate(date);
        var combined = MraOfflineReceiptSigning.GenerateCombinedString(20162939, 1, julian, 1);
        var expected = MraInvoiceNumberGenerator.Generate(20162939, 1, date, 1);
        Assert.Equal(expected, combined);
    }

    [Fact]
    public void BuildParameterString_UsesInvariantCultureAmounts()
    {
        var param = MraOfflineReceiptSigning.BuildParameterString(
            "E-De-JYxh-B",
            numItems: 2,
            invoiceTotal: 100.5m,
            vatAmount: 14.75m,
            julianDateBase64: "JYxh");

        Assert.Equal("TI=E-De-JYxh-B&N=2&I=100.5&V=14.75&T=JYxh", param);
    }

    [Fact]
    public void ComputeHmacWithSha256_IsBase64UrlSafe()
    {
        const string plain = "TI=E-De-JYxh-B&N=1&I=100&V=14&T=JYxh";
        var sig = MraOfflineReceiptSigning.ComputeHmacWithSha256(plain, SecretKey);

        Assert.False(string.IsNullOrWhiteSpace(sig));
        Assert.DoesNotContain("+", sig, StringComparison.Ordinal);
        Assert.DoesNotContain("/", sig, StringComparison.Ordinal);
        Assert.DoesNotContain("=", sig, StringComparison.Ordinal);

        // Manual reference using the same algorithm as the MRA guide.
        var expected = Convert.ToBase64String(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(SecretKey), Encoding.UTF8.GetBytes(plain)))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        Assert.Equal(expected, sig);
        Assert.Equal(expected, HmacSignatureService.ComputeHmacWithSha256(plain, SecretKey));
    }

    [Fact]
    public void Generate_BuildsValidationUrl_WithUrlEncodedS()
    {
        var date = new DateTime(2024, 4, 26, 14, 29, 34, DateTimeKind.Utc);
        var request = new MraOfflineInvoiceSigningRequest
        {
            TaxpayerId = 20162939,
            TerminalPosition = 1,
            TransactionDateUtc = date,
            TransactionCount = 1,
            NumItems = 3,
            InvoiceTotal = 100m,
            VatAmount = 14m
        };

        var result = MraOfflineReceiptSigning.Generate(
            request,
            SecretKey,
            MraOfflineReceiptSigning.DefaultSandboxValidationBaseUrl);

        Assert.Equal(MraInvoiceNumberGenerator.Generate(20162939, 1, date, 1), result.InvoiceNumber);
        Assert.StartsWith("TI=", result.ParameterString, StringComparison.Ordinal);
        Assert.Contains("&N=3&I=100&V=14&T=", result.ParameterString, StringComparison.Ordinal);
        Assert.Equal(
            MraOfflineReceiptSigning.ComputeHmacWithSha256(result.ParameterString, SecretKey),
            result.OfflineDataSignature);
        Assert.Equal(WebUtility.UrlEncode(result.OfflineDataSignature), result.UrlEncodedSignature);
        Assert.StartsWith(
            "https://dev-eis-portal.mra.mw/ReceiptValidation/Validate/?",
            result.ValidationUrl,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.ParameterString.Replace("+", "%2B", StringComparison.Ordinal), result.ValidationUrl, StringComparison.Ordinal);
        Assert.Contains($"&S={result.UrlEncodedSignature}", result.ValidationUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeParameterStringForUrl_EncodesPlusInCompactBase64()
    {
        var raw = "TI=BMwna-B-JY4+-C&N=2&I=6521.25&V=971.25&T=JY4+";
        var encoded = MraOfflineReceiptSigning.EncodeParameterStringForUrl(raw);
        Assert.Equal("TI=BMwna-B-JY4%2B-C&N=2&I=6521.25&V=971.25&T=JY4%2B", encoded);
        // HMAC source must stay unencoded.
        Assert.Contains("+", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("+", encoded, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateFromSalesRequest_UsesLineCountAndTotals()
    {
        var saleBase = SalePayloadFactory.Create("E-De-JYxh-B");
        // Force a known composite invoice so TI parses cleanly.
        var date = new DateTime(2024, 4, 26, 14, 29, 34, DateTimeKind.Utc);
        var invoice = MraInvoiceNumberGenerator.Generate(20162939, 1, date, 7);
        var sale = saleBase with
        {
            InvoiceHeader = new InvoiceHeaderDto
            {
                InvoiceNumber = invoice,
                InvoiceDateTime = date,
                SellerTin = "20162939",
                SiteId = saleBase.InvoiceHeader.SiteId,
                PaymentMethod = saleBase.InvoiceHeader.PaymentMethod,
                GlobalConfigVersion = saleBase.InvoiceHeader.GlobalConfigVersion,
                TaxpayerConfigVersion = saleBase.InvoiceHeader.TaxpayerConfigVersion,
                TerminalConfigVersion = saleBase.InvoiceHeader.TerminalConfigVersion
            },
            InvoiceSummary = saleBase.InvoiceSummary with
            {
                InvoiceTotal = 250.25m,
                TotalVat = 37.25m,
                OfflineSignature = null
            }
        };

        var result = MraOfflineReceiptSigning.GenerateFromSalesRequest(sale, SecretKey);
        Assert.Equal(invoice, result.InvoiceNumber);
        Assert.Equal(sale.InvoiceLineItems.Count, result.NumItems);
        Assert.Equal(250.25m, result.InvoiceTotal);
        Assert.Equal(37.25m, result.VatAmount);
        Assert.Contains(
            FormattableString.Invariant($"&N={sale.InvoiceLineItems.Count}&I=250.25&V=37.25&T="),
            result.ParameterString,
            StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(result.OfflineDataSignature));
        Assert.Contains("ReceiptValidation/Validate", result.ValidationUrl, StringComparison.OrdinalIgnoreCase);
    }
}
