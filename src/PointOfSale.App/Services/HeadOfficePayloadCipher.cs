using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PointOfSale.App.Services;

/// <summary>
/// AES-256-GCM packaging for head-office payloads. Ciphertext is never written to logs.
/// </summary>
public static class HeadOfficePayloadCipher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static byte[] ResolveKey(string? payloadEncryptionKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(payloadEncryptionKeyBase64))
        {
            throw new InvalidOperationException(
                "HeadOfficeSync:PayloadEncryptionKeyBase64 is required to encrypt sync payloads.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(payloadEncryptionKeyBase64.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Head-office encryption key is not valid Base64.", ex);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException("Head-office encryption key must be exactly 32 bytes (AES-256).");
        }

        return key;
    }

    public static string SerializePlainJson<T>(T payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);

    public static EncryptedHeadOfficeEnvelope EncryptJson(string plainJson, byte[] key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainJson);
        ArgumentNullException.ThrowIfNull(key);

        var plaintext = Encoding.UTF8.GetBytes(plainJson);
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return new EncryptedHeadOfficeEnvelope
        {
            Algorithm = "AES-256-GCM",
            NonceBase64 = Convert.ToBase64String(nonce),
            TagBase64 = Convert.ToBase64String(tag),
            CiphertextBase64 = Convert.ToBase64String(ciphertext),
            PlaintextSha256 = Convert.ToHexString(SHA256.HashData(plaintext))
        };
    }

    public static string DecryptToJson(EncryptedHeadOfficeEnvelope envelope, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(key);

        var nonce = Convert.FromBase64String(envelope.NonceBase64);
        var tag = Convert.FromBase64String(envelope.TagBase64);
        var ciphertext = Convert.FromBase64String(envelope.CiphertextBase64);
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}

public sealed class EncryptedHeadOfficeEnvelope
{
    public string Algorithm { get; set; } = "AES-256-GCM";
    public string NonceBase64 { get; set; } = string.Empty;
    public string TagBase64 { get; set; } = string.Empty;
    public string CiphertextBase64 { get; set; } = string.Empty;
    public string? PlaintextSha256 { get; set; }
}
