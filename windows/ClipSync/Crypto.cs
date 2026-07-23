using System.Security.Cryptography;
using System.Text;

namespace ClipSync;

// Chiffrement de bout en bout.
//  - Le secret d'appairage ne quitte JAMAIS l'appareil.
//  - authToken = HKDF(secret, "auth")  → envoyé au serveur pour l'authentification.
//  - encKey    = HKDF(secret, "enc")   → jamais envoyé ; chiffre le contenu (AES-256-GCM).
// Le serveur ne voit donc que du chiffré + un jeton d'auth qui ne permet pas de déchiffrer.
public static class Crypto
{
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("clipsync-v1");

    private static byte[] Derive(string secret, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, Salt, Encoding.UTF8.GetBytes(info));

    public static string AuthToken(string secret) =>
        Convert.ToHexString(Derive(secret, "clipsync-auth-v1")).ToLowerInvariant();

    public static byte[] EncKey(string secret) => Derive(secret, "clipsync-enc-v1");

    // Format du blob : nonce(12) || ciphertext || tag(16).
    public static byte[] Encrypt(byte[] key, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plaintext, ct, tag);

        var blob = new byte[12 + ct.Length + 16];
        Buffer.BlockCopy(nonce, 0, blob, 0, 12);
        Buffer.BlockCopy(ct, 0, blob, 12, ct.Length);
        Buffer.BlockCopy(tag, 0, blob, 12 + ct.Length, 16);
        return blob;
    }

    public static byte[]? Decrypt(byte[] key, byte[] blob)
    {
        if (blob.Length < 28) return null;
        try
        {
            var nonce = blob[..12];
            var tag = blob[^16..];
            var ct = blob[12..^16];
            var pt = new byte[ct.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, ct, tag, pt);
            return pt;
        }
        catch { return null; }
    }

    public static string EncryptText(byte[] key, string text) =>
        Convert.ToBase64String(Encrypt(key, Encoding.UTF8.GetBytes(text)));

    public static string? DecryptText(byte[] key, string base64)
    {
        try
        {
            var pt = Decrypt(key, Convert.FromBase64String(base64));
            return pt is null ? null : Encoding.UTF8.GetString(pt);
        }
        catch { return null; }
    }
}
