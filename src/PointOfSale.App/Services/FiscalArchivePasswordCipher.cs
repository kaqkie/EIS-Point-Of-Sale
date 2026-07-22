using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PointOfSale.App.Services;

/// <summary>
/// Dual-password AES-256-GCM packaging for statutory fiscal archives (.art-fiscal).
/// </summary>
public static class FiscalArchivePasswordCipher
{
    private const int KeyBytes = 32;
    private const int Pbkdf2Iterations = 120_000;

    public static byte[] DeriveDualKey(string primaryPassword, string secondaryPassword)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPassword);
        ArgumentException.ThrowIfNullOrWhiteSpace(secondaryPassword);

        var k1 = Rfc2898DeriveBytes.Pbkdf2(
            primaryPassword,
            Encoding.UTF8.GetBytes("ART-Fiscal-Primary"),
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);
        var k2 = Rfc2898DeriveBytes.Pbkdf2(
            secondaryPassword,
            Encoding.UTF8.GetBytes("ART-Fiscal-Secondary"),
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeyBytes);

        var key = new byte[KeyBytes];
        for (var i = 0; i < KeyBytes; i++)
        {
            key[i] = (byte)(k1[i] ^ k2[i]);
        }

        CryptographicOperations.ZeroMemory(k1);
        CryptographicOperations.ZeroMemory(k2);
        return key;
    }

    public static byte[] Compress(byte[] plaintext)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(plaintext, 0, plaintext.Length);
        }

        return output.ToArray();
    }

    public static byte[] EncryptPayload(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        using var envelope = new MemoryStream(12 + 16 + ciphertext.Length);
        envelope.Write(nonce);
        envelope.Write(tag);
        envelope.Write(ciphertext);
        return envelope.ToArray();
    }

    public static byte[] BuildArtFiscalFile(byte[] manifestJsonUtf8, byte[] payloadJsonUtf8, byte[] key)
    {
        var compressed = Compress(payloadJsonUtf8);
        var encrypted = EncryptPayload(compressed, key);
        var manifestSha = SHA256.HashData(manifestJsonUtf8);

        using var file = new MemoryStream();
        file.Write(Encoding.ASCII.GetBytes("ARTFIS1\n"));
        file.Write(manifestSha);
        file.Write(BitConverter.GetBytes(manifestJsonUtf8.Length));
        file.Write(manifestJsonUtf8);
        file.Write(BitConverter.GetBytes(encrypted.Length));
        file.Write(encrypted);
        return file.ToArray();
    }

    public static string SerializeManifest<T>(T manifest) =>
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
}
