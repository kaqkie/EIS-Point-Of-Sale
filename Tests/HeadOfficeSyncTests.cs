using PointOfSale.App.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class HeadOfficeSyncTests
{
    [Fact]
    public void PayloadCipher_RoundTripsAesGcmJson()
    {
        var key = Convert.FromBase64String(Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()));
        var plain = """{"branchId":"BLTY-01","grossSales":1250.50}""";

        var envelope = HeadOfficePayloadCipher.EncryptJson(plain, key);
        Assert.Equal("AES-256-GCM", envelope.Algorithm);
        Assert.False(string.IsNullOrWhiteSpace(envelope.CiphertextBase64));

        var decrypted = HeadOfficePayloadCipher.DecryptToJson(envelope, key);
        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void PayloadCipher_RejectsNon32ByteKeys()
    {
        var badKey = Convert.ToBase64String(new byte[16]);
        Assert.Throws<InvalidOperationException>(() => HeadOfficePayloadCipher.ResolveKey(badKey));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    public void CatalogConflict_PreservesLocalStockDuringOpenShiftOrUnlessHqOverride(
        bool shiftOpen,
        bool hqOverrideStock,
        bool expectPreserve)
    {
        var preserve = CatalogConflictResolver.ShouldPreserveLocalStock(shiftOpen, hqOverrideStock);
        Assert.Equal(expectPreserve, preserve);
    }

    [Fact]
    public void SyncResult_DisabledFactory_IsSuccessfulNoOp()
    {
        var result = HeadOfficeSyncResult.Disabled("off");
        Assert.False(result.Enabled);
        Assert.True(result.Success);
        Assert.Equal("off", result.Message);
    }
}
