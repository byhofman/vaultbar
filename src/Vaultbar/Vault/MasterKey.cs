using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Vaultbar.Vault;

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
