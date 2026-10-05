using System.Security.Cryptography;
using System.Text;

namespace GarenaOrchestrator;

public sealed class SecretProtector
{
    private readonly byte[] _key;

    public SecretProtector(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 24)
            throw new InvalidOperationException("MASTER_ENCRYPTION_KEY must contain at least 24 characters.");
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    public string HashToken(string token)
    {
        using var hmac = new HMACSHA256(_key);
        return Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(token)));
    }

    public bool VerifyToken(string token, string expectedHash)
    {
        byte[] actual = Encoding.ASCII.GetBytes(HashToken(token));
        byte[] expected = Encoding.ASCII.GetBytes(expectedHash);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public string Encrypt(string value)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plaintext = Encoding.UTF8.GetBytes(value);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        byte[] packed = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, nonce.Length);
        ciphertext.CopyTo(packed, nonce.Length + tag.Length);
        return Convert.ToBase64String(packed);
    }

    public string Decrypt(string value)
    {
        byte[] packed = Convert.FromBase64String(value);
        if (packed.Length < 28) throw new CryptographicException("Invalid protected value.");
        ReadOnlySpan<byte> nonce = packed.AsSpan(0, 12);
        ReadOnlySpan<byte> tag = packed.AsSpan(12, 16);
        ReadOnlySpan<byte> ciphertext = packed.AsSpan(28);
        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    public static string NewSecret(int bytes = 32) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class SafeCompare
{
    public static bool Equals(string? left, string? right)
    {
        if (left is null || right is null) return false;
        byte[] a = Encoding.UTF8.GetBytes(left);
        byte[] b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
