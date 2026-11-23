using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Security;

// Keys for field encryption and the identifier HMAC (dev config here; a KMS/secrets manager in production).
public sealed class SecurityOptions
{
    public const string Section = "Security";

    // Base64 256-bit key. Comes from a KMS in production.
    public string FieldEncryptionKey { get; set; } = "";
    public byte KeyId { get; set; } = 1;

    // Base64 HMAC key for identifiers. Separate from the encryption key.
    public string IdentifierHmacKey { get; set; } = "";
}

// Random tokens. Only the SHA-256 hash is stored.
public static class Tokens
{
    public static string Generate(int bytes = 32) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(bytes));

    public static string Hash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

// AES-256-GCM for PII, on top of disk encryption.
// Format: [keyId:1][nonce:12][tag:16][ciphertext]. Key id lets old rows decrypt after rotation.
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

    // Encrypts plaintext into [keyId][nonce][tag][ciphertext].
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

    // Reverses Protect; throws if the key id or the payload is wrong.
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

// Keyed HMAC for document numbers.
public sealed class IdentifierHasher(IOptions<SecurityOptions> options)
{
    private readonly byte[] _key = Convert.FromBase64String(options.Value.IdentifierHmacKey);

    // Normalises the identifier (alphanumerics only, upper-cased) then HMACs it.
    public string Hash(string identifier)
    {
        var normalised = new string(identifier.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(normalised)));
    }
}
