using System.Security.Cryptography;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public class CryptoTests
{
    [Fact]
    public void EncString_RoundTrips()
    {
        using var key = SymmetricKey.FromBytes(RandomNumberGenerator.GetBytes(64));
        var enc = EncString.Encrypt("héllo wörld 🔐", key);
        Assert.StartsWith("2.", enc);
        Assert.Equal("héllo wörld 🔐", EncString.Decrypt(enc, key));
    }

    [Fact]
    public void EncString_RejectsTamperedMac()
    {
        using var key = SymmetricKey.FromBytes(RandomNumberGenerator.GetBytes(64));
        var parts = EncString.Encrypt("secret", key).Split('|');
        var mac = Convert.FromBase64String(parts[2]);
        mac[0] ^= 1;
        Assert.Throws<CryptographicException>(() => EncString.Decrypt($"{parts[0]}|{parts[1]}|{Convert.ToBase64String(mac)}", key));
    }

    [Fact]
    public void Pbkdf2MasterKey_MatchesReference()
    {
        var key = MasterKey.Derive("asdfasdf", "test@example.com", new KdfConfig(KdfType.Pbkdf2, 100000, null, null));
        var reference = Rfc2898DeriveBytes.Pbkdf2("asdfasdf"u8.ToArray(), "test@example.com"u8.ToArray(), 100000, HashAlgorithmName.SHA256, 32);
        Assert.Equal(reference, key);
    }

    [Fact]
    public void Argon2MasterKey_IsDeterministic()
    {
        var kdf = new KdfConfig(KdfType.Argon2id, 2, 16, 2);
        Assert.Equal(MasterKey.Derive("pw", "salt@example.com", kdf), MasterKey.Derive("pw", "salt@example.com", kdf));
        Assert.Equal(32, MasterKey.Derive("pw", "salt@example.com", kdf).Length);
    }
}
