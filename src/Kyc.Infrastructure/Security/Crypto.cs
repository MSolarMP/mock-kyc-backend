using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Security;

public sealed class SecurityOptions
{
    public const string Section = "Security";

    /// <summary>Base64 256-bit key for field encryption. In production it comes from a KMS/Key Vault and is rotated using <see cref="KeyId"/>.</summary>
    public string FieldEncryptionKey { get; set; } = "";
    public byte KeyId { get; set; } = 1;

    /// <summary>Base64 secret "pepper" for HMAC-ing identifiers. Kept separate from the encryption key.</summary>
    public string IdentifierHmacKey { get; set; } = "";
}

/// <summary>Opaque random tokens. Only their SHA-256 hash is persisted.</summary>
public static class Tokens
{
    public static string Generate(int bytes = 32) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>
/// AES-256-GCM field encryption for PII at rest. This is a second layer on top of disk/database encryption: a
/// backup or a read replica leaking doesn't expose plaintext identity data.
/// Format: [keyId:1][nonce:12][tag:16][ciphertext:n]. The key id lets old rows decrypt after a key rotation.
/// </summary>
public sealed class FieldProtector
{
    private readonly byte[] _key;
    private readonly byte _keyId;

    public FieldProtector(IOptions<SecurityOptions> options)
    {
        _key = Convert.FromBase64String(options.Value.FieldEncryptionKey);
        if (_key.Length != 32) throw new InvalidOperationException("Security:FieldEncryptionKey must be 32 bytes (base64).");
        _keyId = options.Value.KeyId;
    }

    public byte[] Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[1 + 12 + 16 + plain.Length];
        output[0] = _keyId;
        var nonce = output.AsSpan(1, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, output.AsSpan(29), output.AsSpan(13, 16));
        return output;
    }

    public string Unprotect(byte[] payload)
    {
        if (payload.Length < 29 || payload[0] != _keyId)
            throw new CryptographicException("Unknown key id or malformed payload.");
        var plain = new byte[payload.Length - 29];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(payload.AsSpan(1, 12), payload.AsSpan(29), payload.AsSpan(13, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}

/// <summary>
/// Keyed hash for identifiers we need to match on but must not store in plaintext (document numbers).
/// It's keyed (HMAC) so a leaked table can't be brute-forced over the small space of national ID numbers.
/// </summary>
public sealed class IdentifierHasher(IOptions<SecurityOptions> options)
{
    private readonly byte[] _key = Convert.FromBase64String(options.Value.IdentifierHmacKey);

    public string Hash(string identifier)
    {
        var normalised = new string(identifier.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalised)));
    }
}
