using System.Security.Cryptography;
using Vaultbar.Platform;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public sealed class TouchIdTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_dir, "data.json");
    private string KeyPath => Path.Combine(_dir, "touch-id-key.dat");
    private string StorePath => Path.Combine(_dir, "quick-unlock.dat");
    private readonly FakeEnclave _enclave = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class FakeEnclave : ITouchIdDevice
    {
        public bool Available { get; set; } = true;
        public int NextCreate;
        public int NextDerive;
        public int Prompts;

        public int Create(out byte[] blob)
        {
            blob = NextCreate == 0 ? RandomNumberGenerator.GetBytes(64) : [];
            return NextCreate;
        }

        public int Derive(byte[] blob, byte[] challenge, string reason, out byte[] secret)
        {
            Prompts++;
            secret = NextDerive == 0 ? HMACSHA256.HashData(blob, challenge) : [];
            return NextDerive;
        }
    }

    private async Task<VaultService> Enrolled()
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var vault = new VaultService(new Config { DataPath = DataPath }, StorePath) { QuickUnlock = new MacTouchId(_enclave, KeyPath) };
        vault.Probe();
        Assert.Null(await vault.UnlockAsync("correct horse"));
        Assert.Equal(QuickUnlockStatus.Success, await vault.SetUpQuickUnlockAsync());
        vault.Lock();
        return vault;
    }

    [MacFact]
    public async Task SetUp_ThenTouch_UnlocksTheVault()
    {
        var vault = await Enrolled();
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Success, status);
        Assert.Equal("Azure#2026", vault.Items.Single(i => i.Name == "Azure Portal").Password);
    }

    [MacFact]
    public async Task SetUp_AsksForTouchSoABrokenSensorShowsAtOnce()
    {
        await Enrolled();
        Assert.Equal(1, _enclave.Prompts);
    }

    [MacFact]
    public async Task KeyAndStoreFiles_AreOnlyReadableByTheUser()
    {
        if (OperatingSystem.IsWindows()) return;
        await Enrolled();
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(ownerOnly, File.GetUnixFileMode(KeyPath));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(StorePath));
    }

    [MacTheory]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-4)]
    [InlineData(-128)]
    public async Task Canceling_KeepsTheEnrollment(int code)
    {
        var vault = await Enrolled();
        _enclave.NextDerive = code;
        var (status, error) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Canceled, status);
        Assert.Null(error);
        Assert.Equal(VaultState.Locked, vault.State);
        Assert.True(vault.CanQuickUnlock);
    }

    [MacFact]
    public async Task ClosedLid_KeepsTheEnrollmentAndDoesNotAsk()
    {
        var vault = await Enrolled();
        var prompts = _enclave.Prompts;
        _enclave.Available = false;
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Canceled, status);
        Assert.Equal(prompts, _enclave.Prompts);
        Assert.True(vault.CanQuickUnlock);
    }

    [MacTheory]
    [InlineData(-25300)]
    [InlineData(-25293)]
    public async Task ChangedFingers_ForgetTheEnrollmentSoThePasswordCanSetItUpAgain(int code)
    {
        var vault = await Enrolled();
        _enclave.NextDerive = code;
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Unavailable, status);
        Assert.Equal(VaultState.Locked, vault.State);

        _enclave.NextDerive = 0;
        Assert.Null(await vault.UnlockAsync("correct horse"));
        Assert.True(await vault.ShouldOfferQuickUnlockAsync());
    }

    [MacFact]
    public async Task LockedOutSensor_FailsWithoutForgetting()
    {
        var vault = await Enrolled();
        _enclave.NextDerive = -8;
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Failed, status);
        Assert.True(vault.CanQuickUnlock);
    }

    [MacFact]
    public async Task MissingKeyFile_ForgetsTheEnrollment()
    {
        var vault = await Enrolled();
        File.Delete(KeyPath);
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Unavailable, status);
        Assert.False(vault.CanQuickUnlock);
    }

    [MacFact]
    public async Task EnclaveRefusingToCreateAKey_DoesNotEnrollOrLeaveAFile()
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        _enclave.NextCreate = -25308;
        var vault = new VaultService(new Config { DataPath = DataPath }, StorePath) { QuickUnlock = new MacTouchId(_enclave, KeyPath) };
        vault.Probe();
        await vault.UnlockAsync("correct horse");
        Assert.Equal(QuickUnlockStatus.Failed, await vault.SetUpQuickUnlockAsync());
        Assert.False(File.Exists(KeyPath));
        Assert.False(vault.CanQuickUnlock);
    }

    [MacFact]
    public async Task TurningItOff_RemovesTheKeyFile()
    {
        var vault = await Enrolled();
        await vault.QuickUnlockAsync();
        await vault.SetQuickUnlockEnabledAsync(false);
        Assert.False(File.Exists(KeyPath));
        Assert.Equal(QuickUnlockSetting.Off, vault.QuickUnlockSetting);
    }

    /// <summary>A signed build with a keychain access group: the key lives under a per-account tag in the keychain.</summary>
    private sealed class FakeKeychainEnclave : ITouchIdDevice
    {
        public readonly Dictionary<string, byte[]> Keys = new();

        public bool Available => true;
        public bool KeychainAvailable => true;

        public int Create(out byte[] blob) => throw new InvalidOperationException("a signed build never writes a key blob");

        public int Derive(byte[] blob, byte[] challenge, string reason, out byte[] secret) =>
            throw new InvalidOperationException("a signed build never loads a key blob");

        public int CreateInKeychain(string tag)
        {
            Keys[tag] = RandomNumberGenerator.GetBytes(32);
            return 0;
        }

        public int DeriveFromKeychain(string tag, byte[] challenge, string reason, out byte[] secret)
        {
            secret = Keys.TryGetValue(tag, out var key) ? HMACSHA256.HashData(key, challenge) : [];
            return secret.Length > 0 ? 0 : -25300;
        }

        public void DeleteFromKeychain(string tag) => Keys.Remove(tag);
    }

    [MacFact]
    public async Task SignedBuild_KeepsAPerAccountKeyInTheKeychain_AndDropsTheOldBlob()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(KeyPath, RandomNumberGenerator.GetBytes(64));
        var keychain = new FakeKeychainEnclave();
        VaultFixture.Demo().Write(DataPath, "correct horse");
        using var vault = new VaultService(new Config { DataPath = DataPath }, StorePath) { QuickUnlock = new MacTouchId(keychain, KeyPath) };
        vault.Probe();
        Assert.Null(await vault.UnlockAsync("correct horse"));

        Assert.Equal(QuickUnlockStatus.Success, await vault.SetUpQuickUnlockAsync());
        Assert.False(File.Exists(KeyPath));
        Assert.Equal([MacTouchId.KeychainTag(VaultFixture.UserId)], keychain.Keys.Keys);
        Assert.NotEqual(MacTouchId.KeychainTag(VaultFixture.UserId), MacTouchId.KeychainTag("0f9e8d7c-0000-4000-8000-000000000002"));

        vault.Lock();
        Assert.Equal(QuickUnlockStatus.Success, (await vault.QuickUnlockAsync()).Status);

        await vault.SetQuickUnlockEnabledAsync(false);
        Assert.Empty(keychain.Keys);
    }

    [MacFact]
    public void UnsignedBuild_HasNoKeychainAccessGroup()
    {
        Assert.False(new NativeTouchId().KeychainAvailable);
    }
}
