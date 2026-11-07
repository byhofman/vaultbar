using System.Security.Cryptography;
using System.Text;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public sealed class V2KeyTests : IDisposable
{
    private const string Password = "correct horse";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_dir, "data.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    // From bitwarden/sdk-internal, bitwarden-crypto: symmetric_crypto_key.rs and cose/symmetric.rs.
    private const string SdkPaddedXChaChaKey = "pQEEAlDib+JxbqMBlcd3KTUesbufAzoAARFvBIQDBAUGIFggt79surJXmqhPhYuuqi9ZyPfieebmtw2OsmN5SDrb4yUB";
    private const string SdkPaddedXAesKey = "pQEEAlAAAQIDBAUGBwgJCgsMDQ4PAzoAARF5BIIDBCBYIAABAgMEBQYHCAkKCwwNDg8QERITFBUWFxgZGhscHR4fAQ==";

    private static readonly byte[] SdkVectorMessage =
    [
        131, 88, 28, 163, 1, 58, 0, 1, 17, 111, 3, 24, 42, 4, 80, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
        11, 12, 13, 14, 15, 161, 5, 88, 24, 78, 20, 28, 157, 180, 246, 131, 220, 82, 104, 72, 73,
        75, 43, 69, 139, 216, 167, 145, 220, 67, 168, 144, 173, 88, 35, 127, 234, 194, 83, 189,
        172, 65, 29, 156, 73, 98, 87, 231, 87, 129, 15, 235, 127, 125, 97, 211, 51, 212, 211, 2,
        13, 36, 123, 53, 12, 31, 191, 40, 13, 175,
    ];

    private static byte[] Sequence(int length) => Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static SymmetricKey SdkVectorKey() =>
        new(Sequence(32), KeyAlgorithm.XChaCha20Poly1305, Sequence(16), []);

    [Fact]
    public void Encrypt0_MatchesTheBitwardenSdkVector()
    {
        var plain = EncString.DecryptBytes($"7.{Convert.ToBase64String(SdkVectorMessage)}", SdkVectorKey());
        Assert.Equal("Message test vector", Encoding.UTF8.GetString(plain));
    }

    [Theory]
    [InlineData(SdkPaddedXChaChaKey, KeyAlgorithm.XChaCha20Poly1305)]
    [InlineData(SdkPaddedXAesKey, KeyAlgorithm.XAes256Gcm)]
    public void PaddedKeys_FromTheSdkParse_AndKeepTheirBytes(string base64, KeyAlgorithm algorithm)
    {
        var bytes = Convert.FromBase64String(base64);
        using var key = SymmetricKey.FromBytes(bytes);
        Assert.Equal(algorithm, key.Algorithm);
        Assert.Equal(32, key.Enc.Length);
        Assert.Equal(16, key.KeyId!.Length);
        Assert.Equal(bytes, key.ToBytes());
    }

    // draft-irtf-cfrg-xchacha-03, section 2.2.1.
    [Fact]
    public void HChaCha20_MatchesTheDraftVector()
    {
        var subkey = Cose.HChaCha20(Sequence(32), Hex("000000090000004a0000000031415927"));
        Assert.Equal(Hex("82413b4227b27bfed30e42508a877d73a0f9e4d58a74a853c12ec41326d3ecdc"), subkey);
    }

    // https://c2sp.org/XAES-256-GCM
    [Theory]
    [InlineData(1, "", "ce546ef63c9cc60765923609b33a9a1974e96e52daf2fcf7075e2271")]
    [InlineData(3, "c2sp.org/XAES-256-GCM", "986ec1832593df5443a179437fd083bf3fdb41abd740a21f71eb769d")]
    public void XAes256Gcm_MatchesTheSpecVectors(byte keyByte, string aad, string ciphertext)
    {
        var key = Enumerable.Repeat(keyByte, 32).ToArray();
        var plain = Cose.XAes256GcmDecrypt(key, "ABCDEFGHIJKLMNOPQRSTUVWX"u8.ToArray(), Hex(ciphertext), Encoding.ASCII.GetBytes(aad));
        Assert.Equal("XAES-256-GCM", Encoding.ASCII.GetString(plain));
    }

    private async Task<VaultService> Unlock(VaultFixture fixture, string? quickPath = null)
    {
        fixture.Write(DataPath, Password);
        var vault = new VaultService(new Config { DataPath = DataPath }, quickPath ?? Path.Combine(_dir, "quick-unlock.dat"));
        vault.Probe();
        Assert.Null(await vault.UnlockAsync(Password));
        return vault;
    }

    private static VaultFixture Filled(KeyAlgorithm algorithm)
    {
        var fixture = VaultFixture.V2(algorithm);
        var folder = fixture.AddFolder("Work");
        fixture.AddLogin("Plain", "user", "plain-pass", ["https://example.com"], "JBSWY3DPEHPK3PXP", folderId: folder);
        fixture.AddLogin("Long", "user", new string('x', 100), itemKey: true);
        fixture.AddLogin("Empty notes", "user", "", notes: "");
        fixture.AddOrgLogin(fixture.AddOrganization(), "Team wiki", "team", "wiki-pass", itemKey: true);
        return fixture;
    }

    [Theory]
    [InlineData(KeyAlgorithm.XChaCha20Poly1305)]
    [InlineData(KeyAlgorithm.XAes256Gcm)]
    public async Task V2Account_OpensEverything(KeyAlgorithm algorithm)
    {
        using var vault = await Unlock(Filled(algorithm));

        Assert.Equal(0, vault.FailedItems);
        var plain = vault.Items.Single(i => i.Name == "Plain");
        Assert.Equal("plain-pass", plain.Password);
        Assert.Equal("Work", plain.Folder);
        Assert.Equal(new string('x', 100), vault.Items.Single(i => i.Name == "Long").Password);
        Assert.Equal("wiki-pass", vault.Items.Single(i => i.Name == "Team wiki").Password);
    }

    [Fact]
    public async Task V2Account_WrongPasswordIsRefused()
    {
        VaultFixture.V2().Write(DataPath, Password);
        using var vault = new VaultService(new Config { DataPath = DataPath });
        vault.Probe();
        Assert.Equal("Invalid master password.", await vault.UnlockAsync("wrong"));
    }

    [Fact]
    public async Task V2Account_WorksWithQuickUnlockAndRePrompt()
    {
        var device = new FixedDevice();
        var path = Path.Combine(_dir, "quick-unlock.dat");
        var fixture = Filled(KeyAlgorithm.XChaCha20Poly1305);
        fixture.Write(DataPath, Password);
        using var vault = new VaultService(new Config { DataPath = DataPath }, path) { QuickUnlock = device };
        vault.Probe();
        Assert.Null(await vault.UnlockAsync(Password));
        Assert.Equal(QuickUnlockStatus.Success, await vault.SetUpQuickUnlockAsync());

        vault.Lock();
        Assert.Equal(QuickUnlockStatus.Success, (await vault.QuickUnlockAsync()).Status);
        Assert.Equal("plain-pass", vault.Items.Single(i => i.Name == "Plain").Password);
        Assert.Equal(QuickUnlockStatus.Success, await vault.VerifyWithQuickUnlockAsync());
        Assert.True(await vault.VerifyMasterPasswordAsync(Password));
    }

    private sealed class FixedDevice : IQuickUnlockProvider
    {
        private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
        public string Name => "Fixed";
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);
        public Task<(QuickUnlockStatus, byte[]?)> SignAsync(byte[] challenge, bool createKey) =>
            Task.FromResult<(QuickUnlockStatus, byte[]?)>((QuickUnlockStatus.Success, HMACSHA256.HashData(_secret, challenge)));
        public Task DeleteKeyAsync() => Task.CompletedTask;
    }

    [Fact]
    public void TamperedCiphertext_IsRefused()
    {
        var tampered = SdkVectorMessage.ToArray();
        tampered[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => EncString.DecryptBytes($"7.{Convert.ToBase64String(tampered)}", SdkVectorKey()));
    }

    [Fact]
    public void TamperedProtectedHeader_IsRefused()
    {
        using var key = CoseWriter.MakeKey(KeyAlgorithm.XChaCha20Poly1305);
        var bytes = Convert.FromBase64String(CoseWriter.Encrypt("secret"u8.ToArray(), key, 42)[2..]);
        var contentFormatAt = Array.IndexOf(bytes, (byte)42);
        bytes[contentFormatAt] = 60;
        Assert.ThrowsAny<CryptographicException>(() => EncString.DecryptBytes($"7.{Convert.ToBase64String(bytes)}", key));
    }

    [Fact]
    public void MessageForAnotherKeyId_IsRefused()
    {
        using var key = CoseWriter.MakeKey(KeyAlgorithm.XChaCha20Poly1305);
        var message = CoseWriter.Encrypt("secret"u8.ToArray(), key, 42, keyIdOverride: new byte[16]);
        Assert.ThrowsAny<CryptographicException>(() => EncString.DecryptBytes(message, key));
    }

    [Fact]
    public void MessageNamingAnotherAlgorithm_IsRefused()
    {
        using var key = CoseWriter.MakeKey(KeyAlgorithm.XChaCha20Poly1305);
        var message = CoseWriter.Encrypt("secret"u8.ToArray(), key, 42, algOverride: -70010);
        Assert.ThrowsAny<CryptographicException>(() => EncString.DecryptBytes(message, key));
    }

    [Fact]
    public void UnknownContentType_IsRefused()
    {
        using var key = CoseWriter.MakeKey(KeyAlgorithm.XChaCha20Poly1305);
        var message = CoseWriter.Encrypt("secret"u8.ToArray(), key, "text/plain");
        Assert.ThrowsAny<CryptographicException>(() => EncString.DecryptBytes(message, key));
    }

    [Theory]
    [InlineData(KeyAlgorithm.AesCbcHmac)]
    [InlineData(KeyAlgorithm.XChaCha20Poly1305)]
    public void StrippedMac_IsNotAcceptedByAStrongerKey(KeyAlgorithm algorithm)
    {
        using var key = algorithm == KeyAlgorithm.AesCbcHmac ? SymmetricKey.FromBytes(RandomNumberGenerator.GetBytes(64)) : CoseWriter.MakeKey(algorithm);
        var downgraded = "0.NQfjHLr6za7VQVAbrpL81w==|wfrjmyJ0bfwkQlySrhw8dA==";
        Assert.Throws<CryptographicException>(() => EncString.DecryptBytes(downgraded, key));
    }

    [Fact]
    public void LegacyMessage_IsNotAcceptedByACoseKey()
    {
        using var legacy = SymmetricKey.FromBytes(RandomNumberGenerator.GetBytes(64));
        using var cose = CoseWriter.MakeKey(KeyAlgorithm.XChaCha20Poly1305);
        Assert.Throws<CryptographicException>(() => EncString.DecryptBytes(EncString.Encrypt("secret", legacy), cose));
    }

    [Fact]
    public void CoseMessage_IsNotAcceptedByALegacyKey()
    {
        using var legacy = SymmetricKey.FromBytes(RandomNumberGenerator.GetBytes(64));
        var message = $"7.{Convert.ToBase64String(SdkVectorMessage)}";
        Assert.Throws<CryptographicException>(() => EncString.DecryptBytes(message, legacy));
    }

    [Theory]
    [InlineData("oA==")]
    [InlineData("gw==")]
    [InlineData("gkA=")]
    [InlineData("//8=")]
    public void GarbageMessages_AreRefused(string base64)
    {
        Assert.ThrowsAny<CryptographicException>(() => EncString.DecryptBytes($"7.{base64}", SdkVectorKey()));
    }

    [Fact]
    public void UnknownKeyAlgorithm_SaysSo()
    {
        var padded = Convert.FromBase64String(SdkPaddedXChaChaKey);
        // Label 3's value: -70000 becomes -70001, which no version of the SDK uses.
        var at = padded.AsSpan().IndexOf(new byte[] { 0x3a, 0x00, 0x01, 0x11, 0x6f });
        padded[at + 4] = 0x70;
        Assert.Throws<NotSupportedException>(() => SymmetricKey.FromBytes(padded));
    }
}
