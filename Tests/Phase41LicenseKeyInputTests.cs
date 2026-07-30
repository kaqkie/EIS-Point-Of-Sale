using System.Text.RegularExpressions;
using PointOfSale.App.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class Phase41LicenseKeyInputTests
{
    [Fact]
    public void ExactFormatPattern_MatchesRequiredSchema()
    {
        Assert.Equal(
            @"^[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$",
            LicenseKeyInputFormatter.ExactFormatPattern);
        Assert.Matches(LicenseKeyInputFormatter.ExactFormatPattern, "I4CV-M5YY-AKY6-Z9BT");
        Assert.DoesNotMatch(LicenseKeyInputFormatter.ExactFormatPattern, "I4CV-M5YY-AKY6");
        Assert.DoesNotMatch(LicenseKeyInputFormatter.ExactFormatPattern, "i4cv-m5yy-aky6-z9bt");
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("i4cv", "I4CV")]
    [InlineData("i4cvm5", "I4CV-M5")]
    [InlineData("i4cv m5yy aky6 z9bt", "I4CV-M5YY-AKY6-Z9BT")]
    [InlineData("I4CV-M5YY-AKY6-Z9BT", "I4CV-M5YY-AKY6-Z9BT")]
    [InlineData("I4CV--M5YY!!AKY6__Z9BTEXTRA", "I4CV-M5YY-AKY6-Z9BT")]
    public void ApplyMask_UppercasesAndInsertsHyphens(string? input, string expected)
    {
        Assert.Equal(expected, LicenseKeyInputFormatter.ApplyMask(input));
        Assert.True(LicenseKeyInputFormatter.ApplyMask(input).Length <= LicenseKeyInputFormatter.MaxLength);
    }

    [Fact]
    public void ApplyMask_CapsAtNineteenCharacters()
    {
        var masked = LicenseKeyInputFormatter.ApplyMask("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
        Assert.Equal(19, masked.Length);
        Assert.Equal("ABCD-EFGH-IJKL-MNOP", masked);
        Assert.True(LicenseKeyInputFormatter.IsExactFormat(masked));
    }

    [Fact]
    public void IsExactFormat_RequiresFullDashedGroups()
    {
        Assert.False(LicenseKeyInputFormatter.IsExactFormat("I4CV-M5YY-AKY6"));
        Assert.True(LicenseKeyInputFormatter.IsExactFormat("I4CV-M5YY-AKY6-Z9BT"));
    }

    [Fact]
    public void PartialEntry_DoesNotShowFormatError()
    {
        Assert.True(LicenseKeyInputFormatter.IsValidPartial("I4CV-M5"));
        Assert.True(LicenseKeyInputFormatter.IsIncomplete("I4CV-M5"));
        Assert.False(LicenseKeyInputFormatter.ShouldShowFormatError("I4CV-M5"));
        Assert.Equal(
            LicenseKeyInputFormatter.IncompleteHintMessage,
            LicenseKeyInputFormatter.GetLiveFeedbackMessage("I4CV-M5"));
    }

    [Fact]
    public void BrokenStructure_ShowsFormatError()
    {
        // Bypass ApplyMask — simulate a non-masked binding value.
        const string broken = "I4CV-M5YY-AKY6-Z9BTX";
        Assert.Equal(20, broken.Length);
        Assert.True(LicenseKeyInputFormatter.ShouldShowFormatError(broken));
        Assert.Equal(
            LicenseKeyInputFormatter.FormatErrorMessage,
            LicenseKeyInputFormatter.GetLiveFeedbackMessage(broken));
    }

    [Fact]
    public void MaxLengthConstant_IsNineteen()
    {
        Assert.Equal(19, LicenseKeyInputFormatter.MaxLength);
        Assert.Equal("XXXX-XXXX-XXXX-XXXX", LicenseKeyInputFormatter.Placeholder);
    }

    [Fact]
    public void GeneratedExactRegex_IsCultureInvariant()
    {
        Assert.Matches(
            new Regex(LicenseKeyInputFormatter.ExactFormatPattern, RegexOptions.CultureInvariant),
            "ABCD-EFGH-IJKL-MNOP");
    }
}
