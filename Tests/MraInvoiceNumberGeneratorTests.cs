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
    public void NeedsInvoiceNumberRewrite_DetectsLegacyArtAndTinMismatch()
    {
        Assert.True(MraInvoiceNumberGenerator.NeedsInvoiceNumberRewrite("ART-20260724164619", "20162939"));
        Assert.True(MraInvoiceNumberGenerator.NeedsInvoiceNumberRewrite(null, "20162939"));

        var correct = MraInvoiceNumberGenerator.Generate(20162939, 1, new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc), 3);
        Assert.False(MraInvoiceNumberGenerator.NeedsInvoiceNumberRewrite(correct, "20162939"));

        // Composite that encodes placeholder TIN 1234567890 must be rewritten for real TIN.
        var wrongTin = MraInvoiceNumberGenerator.Generate(1234567890, 1, new DateTime(2026, 7, 24, 0, 0, 0, DateTimeKind.Utc), 5);
        Assert.True(MraInvoiceNumberGenerator.NeedsInvoiceNumberRewrite(wrongTin, "20162939"));
        Assert.True(MraInvoiceNumberGenerator.TryGetEncodedTaxpayerId(wrongTin, out var encoded));
        Assert.Equal(1234567890, encoded);
        Assert.False(MraInvoiceNumberGenerator.NeedsInvoiceNumberRewrite(wrongTin, "1234567890"));

        // Fiscal taxpayerId 11234 (Cvi) differs from seller TIN 20122074 (BMwna).
        var tinEncoded = MraInvoiceNumberGenerator.Generate(20122074, 1, new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc), 1);
        Assert.True(MraInvoiceNumberGenerator.NeedsInvoiceNumberRewrite(
            tinEncoded,
            sellerTin: "20122074",
            fiscalTaxpayerId: 11234));
        var fiscalEncoded = MraInvoiceNumberGenerator.Generate(11234, 38, new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc), 1);
        Assert.StartsWith("Cvi-", fiscalEncoded, StringComparison.Ordinal);
        Assert.False(MraInvoiceNumberGenerator.NeedsInvoiceNumberRewrite(
            fiscalEncoded,
            sellerTin: "20122074",
            fiscalTaxpayerId: 11234));
    }

    [Fact]
    public void Generate_RejectsZeroTransactionCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MraInvoiceNumberGenerator.Generate(20162939, 1, DateTime.UtcNow, 0));
    }

    [Fact]
    public void TryBase64ToBase10_RoundTripsTaxpayerId()
    {
        var segment = MraInvoiceNumberGenerator.Base10ToBase64(20162939);
        Assert.True(MraInvoiceNumberGenerator.TryBase64ToBase10(segment, out var value));
        Assert.Equal(20162939, value);
    }
}
