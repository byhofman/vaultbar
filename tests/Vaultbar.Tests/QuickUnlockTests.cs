using System.Security.Cryptography;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public class QuickUnlockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_dir, "data.json");
    private string StorePath => Path.Combine(_dir, "quick-unlock.dat");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class FakeDevice : IQuickUnlockProvider
    {
        public byte[] Secret = RandomNumberGenerator.GetBytes(32);
        public QuickUnlockStatus Next = QuickUnlockStatus.Success;
        public int Prompts;
        public bool Deleted;

        public string Name => "Fake Hello";
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);

        public Task<(QuickUnlockStatus Status, byte[]? Signature)> SignAsync(byte[] challenge, bool createKey)
        {
            Prompts++;
            if (createKey) Secret = RandomNumberGenerator.GetBytes(32);
            return Task.FromResult<(QuickUnlockStatus, byte[]?)>(Next == QuickUnlockStatus.Success
                ? (Next, HMACSHA256.HashData(Secret, challenge))
                : (Next, null));
        }

        public Task DeleteKeyAsync()
        {
            Deleted = true;
            return Task.CompletedTask;
        }
    }

    private async Task<(VaultService Vault, FakeDevice Device)> Enrolled()
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var device = new FakeDevice();
        var vault = new VaultService(new Config { DataPath = DataPath }, StorePath) { QuickUnlock = device };
        vault.Probe();
        Assert.Equal(QuickUnlockSetting.NotSetUp, vault.QuickUnlockSetting);
        Assert.Null(await vault.UnlockAsync("correct horse"));
        Assert.True(await vault.ShouldOfferQuickUnlockAsync());
        Assert.Equal(QuickUnlockStatus.Success, await vault.SetUpQuickUnlockAsync());
        Assert.True(vault.CanQuickUnlock);
        return (vault, device);
    }

    [Fact]
    public async Task SetUp_ThenUnlockWithDevice_DecryptsVault()
    {
        var (vault, _) = await Enrolled();
        vault.Lock();
        var (status, error) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Success, status);
        Assert.Null(error);
        Assert.Equal(VaultState.Unlocked, vault.State);
        Assert.Equal("Azure#2026", vault.Items.Single(i => i.Name == "Azure Portal").Password);
    }

    [Fact]
    public async Task AfterDeviceUnlock_MasterPasswordAndDeviceStillVerify()
    {
        var (vault, _) = await Enrolled();
        vault.Lock();
        await vault.QuickUnlockAsync();
        Assert.True(await vault.VerifyMasterPasswordAsync("correct horse"));
        Assert.False(await vault.VerifyMasterPasswordAsync("nope"));
        Assert.Equal(QuickUnlockStatus.Success, await vault.VerifyWithQuickUnlockAsync());
    }

    [Fact]
    public async Task WrongDeviceKey_FailsAndForgetsEnrollment()
    {
        var (vault, device) = await Enrolled();
        vault.Lock();
        device.Secret = RandomNumberGenerator.GetBytes(32);
        var (status, error) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Failed, status);
        Assert.NotNull(error);
        Assert.Equal(VaultState.Locked, vault.State);
        Assert.Equal(QuickUnlockSetting.NotSetUp, vault.QuickUnlockSetting);
    }

    [Fact]
    public async Task StoredKeyFromAnotherVault_IsRejected()
    {
        var (vault, _) = await Enrolled();
        vault.Lock();
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Failed, status);
        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public async Task CanceledSetUp_IsRemembered_AndCanBeTurnedBackOn()
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var device = new FakeDevice { Next = QuickUnlockStatus.Canceled };
        var vault = new VaultService(new Config { DataPath = DataPath }, StorePath) { QuickUnlock = device };
        vault.Probe();
        await vault.UnlockAsync("correct horse");
        Assert.Equal(QuickUnlockStatus.Canceled, await vault.SetUpQuickUnlockAsync());
        Assert.Equal(QuickUnlockSetting.Off, vault.QuickUnlockSetting);
        Assert.False(await vault.ShouldOfferQuickUnlockAsync());

        device.Next = QuickUnlockStatus.Success;
        await vault.SetQuickUnlockEnabledAsync(true);
        Assert.Equal(QuickUnlockSetting.On, vault.QuickUnlockSetting);

        await vault.SetQuickUnlockEnabledAsync(false);
        Assert.Equal(QuickUnlockSetting.Off, vault.QuickUnlockSetting);
        Assert.True(device.Deleted);
    }
}
