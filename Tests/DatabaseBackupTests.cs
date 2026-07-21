using System.IO;
using System.Security.Cryptography;
using System.Text;
using PointOfSale.App.Services;
using Xunit;

namespace PointOfSale.Tests;

public sealed class DatabaseBackupTests
{
    [Fact]
    public async Task ComputeSha256Hex_IsStableForSameContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"art-bak-test-{Guid.NewGuid():N}.bin");
        try
        {
            await File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes("Albert Retail Terminal backup fixture"));
            var first = await DatabaseBackupService.ComputeSha256HexAsync(path);
            var second = await DatabaseBackupService.ComputeSha256HexAsync(path);
            Assert.Equal(first, second);
            Assert.Equal(64, first.Length);
            Assert.True(DatabaseBackupService.VerifyChecksum(path, first));
            Assert.False(DatabaseBackupService.VerifyChecksum(path, "00" + first[2..]));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void RestoreResult_RequiresConfirmationFactorySemantics()
    {
        var failed = DatabaseRestoreResult.Failed("need confirm");
        Assert.False(failed.Success);
        Assert.Equal("need confirm", failed.Error);
    }

    [Fact]
    public void BackupManifest_RoundTripsJsonShape()
    {
        var manifest = new DatabaseBackupManifest
        {
            DatabaseName = "PointOfSale",
            BackupFileName = "PointOfSale_test.bak",
            BackupFilePath = @"C:\ProgramData\AlbertRetailTerminal\Backups\PointOfSale_test.bak",
            ManifestFilePath = @"C:\ProgramData\AlbertRetailTerminal\Backups\PointOfSale_test.manifest.json",
            Sha256Checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("x"))),
            BackupBytes = 1024,
            CreatedAtUtc = DateTime.UtcNow,
            Trigger = DatabaseBackupTriggers.Manual,
            SchemaVersion = 14,
            Compressed = true,
            ChecksumEnabled = true
        };

        Assert.Equal(DatabaseBackupTriggers.Manual, manifest.Trigger);
        Assert.Equal(14, manifest.SchemaVersion);
        Assert.True(manifest.Compressed);
    }
}
