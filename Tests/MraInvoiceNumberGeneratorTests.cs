using PointOfSale.Mra.Billing;
using Xunit;

namespace PointOfSale.Tests;

public sealed class MraInvoiceNumberGeneratorTests
{
    [Theory]
    [InlineData(0, "A")]
    [InlineData(1, "B")]
    [InlineData(63, "/")]
    [InlineData(64, "BA")]
    public void Base10ToBase64_UsesMraAlphabet(long value, string expected) =>
        Assert.Equal(expected, MraInvoiceNumberGenerator.Base10ToBase64(value));

    [Fact]
    public void Generate_UsesHyphenSeparatedSegments()
    {
        var date = new DateTime(2026, 7, 27, 10, 51, 31, DateTimeKind.Utc);
        var invoiceNumber = MraInvoiceNumberGenerator.Generate(20162939, 1, date, 1);

        var parts = invoiceNumber.Split('-');
        Assert.Equal(4, parts.Length);
        Assert.Equal(MraInvoiceNumberGenerator.Base10ToBase64(20162939), parts[0]);
        Assert.Equal(MraInvoiceNumberGenerator.Base10ToBase64(1), parts[1]);
        Assert.Equal(MraInvoiceNumberGenerator.Base10ToBase64(MraInvoiceNumberGenerator.ToJulianDate(date)), parts[2]);
        Assert.Equal(MraInvoiceNumberGenerator.Base10ToBase64(1), parts[3]);
    }

    [Fact]
    public void TryParseTaxpayerId_ExtractsDigits()
    {
        Assert.True(MraInvoiceNumberGenerator.TryParseTaxpayerId("20162939", out var id));
        Assert.Equal(20162939, id);
        Assert.True(MraInvoiceNumberGenerator.TryParseTaxpayerId("TIN-20162939-X", out id));
        Assert.Equal(20162939, id);
    }
}
