using System.Security.Cryptography;
using System.Text;

namespace CertWatch.Core.History;

/// <summary>
/// Encryption for the history database. Sensitive fields (sites, IPs, certificate details, egress)
/// are sealed with AES-256-GCM; rows are found by a keyed hash of the site (HMAC-SHA256) so lookups
/// work without the site name being stored in the clear. Both keys derive from one random 32-byte
/// master key, which the app keeps protected with Windows DPAPI under the user's account.
/// </summary>
public sealed class HistoryCipher
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] AssociatedData = "certwatch-history-v1"u8.ToArray();

    private readonly byte[] _encryptionKey;
    private readonly byte[] _siteKey;

    public HistoryCipher(byte[] masterKey)
    {
        if (masterKey.Length != 32) throw new ArgumentException("Master key must be 32 bytes.", nameof(masterKey));
        _encryptionKey = HMACSHA256.HashData(masterKey, "certwatch-encryption-v1"u8);
        _siteKey = HMACSHA256.HashData(masterKey, "certwatch-site-lookup-v1"u8);
    }

    public static byte[] NewMasterKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Stable, keyed lookup value for a site. Reveals nothing about the site without the key.</summary>
    public string SiteKey(string host, int port) =>
        Convert.ToHexString(HMACSHA256.HashData(_siteKey, Encoding.UTF8.GetBytes($"{host.ToLowerInvariant()}:{port}")));

    public string Seal(string plaintext)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        var output = new byte[NonceSize + TagSize + data.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize), AssociatedData);
        return Convert.ToBase64String(output);
    }

    /// <summary>Decrypts a sealed value; null when it was tampered with or sealed under another key.</summary>
    public string? Open(string sealedValue)
    {
        try
        {
            var input = Convert.FromBase64String(sealedValue);
            if (input.Length < NonceSize + TagSize) return null;
            var plain = new byte[input.Length - NonceSize - TagSize];
            using var aes = new AesGcm(_encryptionKey, TagSize);
            aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plain, AssociatedData);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
