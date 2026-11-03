using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Vaultbar.Vault;

public sealed class SymmetricKey : IDisposable
{
    public SymmetricKey(byte[] enc, byte[]? mac)
    {
        Enc = enc;
        Mac = mac;
    }

    public byte[] Enc { get; }
    public byte[]? Mac { get; }

    public static SymmetricKey FromBytes(ReadOnlySpan<byte> key) => key.Length switch
    {
        64 => new SymmetricKey(key[..32].ToArray(), key[32..].ToArray()),
        32 => new SymmetricKey(key.ToArray(), null),
        _ => throw new CryptographicException($"Unexpected key length {key.Length}"),
    };

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Enc);
        if (Mac != null) CryptographicOperations.ZeroMemory(Mac);
    }
}

/// <summary>Bitwarden "EncString": "&lt;type&gt;.&lt;b64 iv&gt;|&lt;b64 ciphertext&gt;|&lt;b64 mac&gt;".</summary>
public static class EncString
{
    public static int TypeOf(string s) => Parse(s).Type;

    public static byte[] DecryptBytes(string s, SymmetricKey key)
    {
        var (type, parts) = Parse(s);
        switch (type)
        {
            case 0:
                return AesDecrypt(key.Enc, B64(parts, 0), B64(parts, 1));
            case 2:
            {
                if (key.Mac is null) throw new CryptographicException("Key has no MAC component");
                var iv = B64(parts, 0);
                var ct = B64(parts, 1);
                var mac = B64(parts, 2);
                var macData = new byte[iv.Length + ct.Length];
                iv.CopyTo(macData, 0);
                ct.CopyTo(macData, iv.Length);
                var computed = HMACSHA256.HashData(key.Mac, macData);
                if (!CryptographicOperations.FixedTimeEquals(computed, mac))
                    throw new CryptographicException("MAC mismatch");
                return AesDecrypt(key.Enc, iv, ct);
            }
            default:
                throw new NotSupportedException($"Unsupported EncString type {type}");
        }
    }

    public static string Decrypt(string s, SymmetricKey key)
    {
        var bytes = DecryptBytes(s, key);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static byte[] DecryptRsa(string s, RSA rsa)
    {
        var (type, parts) = Parse(s);
        var ct = B64(parts, 0);
        return type switch
        {
            3 or 5 => rsa.Decrypt(ct, RSAEncryptionPadding.OaepSHA256),
            4 or 6 => rsa.Decrypt(ct, RSAEncryptionPadding.OaepSHA1),
            _ => throw new NotSupportedException($"Unsupported asymmetric EncString type {type}"),
        };
    }

    public static string Encrypt(ReadOnlySpan<byte> plain, SymmetricKey key)
    {
        if (key.Mac is null) throw new CryptographicException("Key has no MAC component");
        var iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = key.Enc;
        var ct = aes.EncryptCbc(plain, iv);
        var macData = new byte[iv.Length + ct.Length];
        iv.CopyTo(macData, 0);
        ct.CopyTo(macData, iv.Length);
        var mac = HMACSHA256.HashData(key.Mac, macData);
        return $"2.{Convert.ToBase64String(iv)}|{Convert.ToBase64String(ct)}|{Convert.ToBase64String(mac)}";
    }

    public static string Encrypt(string plain, SymmetricKey key) => Encrypt(Encoding.UTF8.GetBytes(plain), key);

    private static (int Type, string[] Parts) Parse(string s)
    {
        var dot = s.IndexOf('.');
        if (dot > 0 && dot < 3 && int.TryParse(s.AsSpan(0, dot), out var type))
            return (type, s[(dot + 1)..].Split('|'));
        var parts = s.Split('|');
        return (parts.Length == 3 ? 2 : 0, parts);
    }

    private static byte[] B64(string[] parts, int i) =>
        i < parts.Length ? Convert.FromBase64String(parts[i]) : throw new CryptographicException("Malformed EncString");

    private static byte[] AesDecrypt(byte[] key, byte[] iv, byte[] ct)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(ct, iv);
    }
}

public enum KdfType
{
    Pbkdf2 = 0,
    Argon2id = 1,
}

public sealed record KdfConfig(KdfType Type, int Iterations, int? MemoryMiB, int? Parallelism);

public static class MasterKey
{
    public static byte[] Derive(string password, string salt, KdfConfig kdf)
    {
        var pw = Encoding.UTF8.GetBytes(password);
        try
        {
            switch (kdf.Type)
            {
                case KdfType.Pbkdf2:
                    return Rfc2898DeriveBytes.Pbkdf2(pw, Encoding.UTF8.GetBytes(salt), kdf.Iterations, HashAlgorithmName.SHA256, 32);
                case KdfType.Argon2id:
                    var argon = new Argon2id(pw)
                    {
                        Salt = SHA256.HashData(Encoding.UTF8.GetBytes(salt)),
                        Iterations = kdf.Iterations,
                        MemorySize = (kdf.MemoryMiB ?? 64) * 1024,
                        DegreeOfParallelism = kdf.Parallelism ?? 4,
                    };
                    return argon.GetBytes(32);
                default:
                    throw new NotSupportedException($"Unsupported KDF {kdf.Type}");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
        }
    }

    public static SymmetricKey Stretch(byte[] masterKey) => new(
        HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, "enc"u8.ToArray()),
        HKDF.Expand(HashAlgorithmName.SHA256, masterKey, 32, "mac"u8.ToArray()));
}
