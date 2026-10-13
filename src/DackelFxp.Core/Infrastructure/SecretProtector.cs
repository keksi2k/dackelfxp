using System.Security.Cryptography;
using System.Text;

namespace DackelFxp.Core.Infrastructure;

/// <summary>
/// Encrypts passwords with AES-GCM. The key is either derived from a master password (PBKDF2)
/// or, without master password, a random key stored in a user-only key file.
/// Format: "v1:" + base64(nonce | tag | ciphertext).
/// </summary>
public sealed class SecretProtector
{
    private const string Prefix = "v1:";
    private const int Iterations = 600_000;
    private readonly byte[] _key;

    private SecretProtector(byte[] key) => _key = key;

    public static SecretProtector FromKeyFile(string keyFile)
    {
        if (File.Exists(keyFile))
        {
            var existing = File.ReadAllBytes(keyFile);
            if (existing.Length == 32) return new SecretProtector(existing);
        }
        var key = RandomNumberGenerator.GetBytes(32);
        File.WriteAllBytes(keyFile, key);
        AppPaths.RestrictToUser(keyFile);
        return new SecretProtector(key);
    }

    public static SecretProtector FromMasterPassword(string password, byte[] salt) =>
        new(Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32));

    public static byte[] NewSalt() => RandomNumberGenerator.GetBytes(16);

    /// <summary>A value to store alongside the data to check a master password without decrypting everything.</summary>
    public string CreateVerifier() => Protect("dackelfxp");

    public bool Verify(string verifier)
    {
        try { return Unprotect(verifier) == "dackelfxp"; }
        catch (CryptographicException) { return false; }
    }

    public string Protect(string plain)
    {
        if (plain.Length == 0) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var cipher = new byte[data.Length];
        using (var aes = new AesGcm(_key, tag.Length))
            aes.Encrypt(nonce, data, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public string Unprotect(string protectedValue)
    {
        if (protectedValue.Length == 0) return "";
        if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
            throw new CryptographicException("Unbekanntes Format");
        var raw = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        var nonce = raw.AsSpan(0, nonceSize);
        var tag = raw.AsSpan(nonceSize, tagSize);
        var cipher = raw.AsSpan(nonceSize + tagSize);
        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(_key, tagSize))
            aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
