using System.Security.Cryptography;
using System.Text.Json.Nodes;
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

        public Task<(QuickUnlockStatus Status, byte[]? Signature)> SignAsync(string account, byte[] challenge, bool createKey)
        {
            Prompts++;
            if (createKey) Secret = RandomNumberGenerator.GetBytes(32);
            return Task.FromResult<(QuickUnlockStatus, byte[]?)>(Next == QuickUnlockStatus.Success
                ? (Next, HMACSHA256.HashData(Secret, challenge))
                : (Next, null));
        }

        public Task DeleteKeyAsync(string account)
        {
            Deleted = true;
            return Task.CompletedTask;
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();

    private VaultService Restarted(FakeDevice device, bool acrossRestarts = false)
    {
        var vault = new VaultService(new Config { DataPath = DataPath, QuickUnlockAcrossRestarts = acrossRestarts }, StorePath, _clock) { QuickUnlock = device };
        vault.Probe();
        return vault;
    }

    private async Task<(VaultService Vault, FakeDevice Device)> Enrolled(bool acrossRestarts = false)
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var device = new FakeDevice();
        var vault = new VaultService(new Config { DataPath = DataPath, QuickUnlockAcrossRestarts = acrossRestarts }, StorePath, _clock) { QuickUnlock = device };
        vault.Probe();
        Assert.Equal(QuickUnlockSetting.NotSetUp, vault.QuickUnlockSetting);
        Assert.Null(await vault.UnlockAsync("correct horse"));
        Assert.True(await vault.ShouldOfferQuickUnlockAsync());
        Assert.Equal(QuickUnlockStatus.Success, await vault.SetUpQuickUnlockAsync());
        vault.Lock();
        Assert.True(vault.CanQuickUnlock);
        Assert.Null(await vault.UnlockAsync("correct horse"));
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

    [Fact]
    public async Task ByDefault_NoKeyIsStored_SoARestartNeedsTheMasterPassword()
    {
        var (vault, device) = await Enrolled();
        vault.Lock();
        Assert.Null(new QuickUnlockStore(StorePath).Load(VaultFixture.UserId, DateTimeOffset.MinValue)?.WrappedKey);
        vault.Dispose();

        using var restarted = Restarted(device);
        Assert.Equal(QuickUnlockSetting.On, restarted.QuickUnlockSetting);
        Assert.False(restarted.CanQuickUnlock);
        Assert.Equal(QuickUnlockStatus.Unavailable, (await restarted.QuickUnlockAsync()).Status);
        Assert.Equal(VaultState.Locked, restarted.State);

        Assert.Null(await restarted.UnlockAsync("correct horse"));
        restarted.Lock();
        Assert.True(restarted.CanQuickUnlock);
        Assert.Equal(QuickUnlockStatus.Success, (await restarted.QuickUnlockAsync()).Status);
    }

    [Fact]
    public async Task AcrossRestarts_IsOptIn_AndEndsAtTheMaximumAge()
    {
        var (vault, device) = await Enrolled(acrossRestarts: true);
        vault.Dispose();

        _clock.Now += TimeSpan.FromDays(13);
        using (var restarted = Restarted(device, acrossRestarts: true))
        {
            Assert.True(restarted.CanQuickUnlock);
            Assert.Equal(QuickUnlockStatus.Success, (await restarted.QuickUnlockAsync()).Status);
        }

        // The age counts from the master password, not from the last quick unlock.
        _clock.Now += TimeSpan.FromDays(2);
        using var expired = Restarted(device, acrossRestarts: true);
        Assert.False(expired.CanQuickUnlock);
        Assert.Equal(QuickUnlockStatus.Unavailable, (await expired.QuickUnlockAsync()).Status);
        Assert.Null(new QuickUnlockStore(StorePath).Load(VaultFixture.UserId, DateTimeOffset.MinValue)?.WrappedKey);

        // A master-password unlock starts the age again; the next quick unlock stores the key, with no extra prompt.
        Assert.Null(await expired.UnlockAsync("correct horse"));
        expired.Lock();
        Assert.Equal(QuickUnlockStatus.Success, (await expired.QuickUnlockAsync()).Status);
        expired.Dispose();
        using var again = Restarted(device, acrossRestarts: true);
        Assert.True(again.CanQuickUnlock);
    }

    [Fact]
    public async Task TurningPersistenceOff_RemovesTheStoredKey()
    {
        var (vault, device) = await Enrolled(acrossRestarts: true);
        vault.Dispose();

        using var restarted = Restarted(device, acrossRestarts: false);
        Assert.False(restarted.CanQuickUnlock);
        Assert.Null(new QuickUnlockStore(StorePath).Load(VaultFixture.UserId, DateTimeOffset.MinValue)?.WrappedKey);
    }

    [Fact]
    public async Task AnAccountThatLeavesDataJson_TakesItsQuickUnlockWithIt()
    {
        var (vault, device) = await Enrolled(acrossRestarts: true);
        vault.Lock();

        var root = JsonNode.Parse(File.ReadAllText(DataPath))!.AsObject();
        const string other = "0f9e8d7c-0000-4000-8000-000000000002";
        root["global_account_activeAccountId"] = other;
        root["global_account_accounts"] = new JsonObject { [other] = new JsonObject { ["email"] = "sam@fabrikam.com" } };
        foreach (var key in root.Select(p => p.Key).Where(k => k.StartsWith($"user_{VaultFixture.UserId}_", StringComparison.Ordinal)).ToList())
            root[key.Replace(VaultFixture.UserId, other)] = root[key]!.DeepClone();
        File.WriteAllText(DataPath, root.ToJsonString());
        vault.Probe();

        Assert.False(File.Exists(StorePath));
        Assert.False(vault.CanQuickUnlock);
        Assert.Equal(QuickUnlockStatus.Unavailable, (await vault.QuickUnlockAsync()).Status);
    }

    [Fact]
    public async Task AnEnrollmentFromBeforeVbr25_IsNotKept()
    {
        // Written as plain JSON, which on Windows would also need the DPAPI layer.
        if (OperatingSystem.IsWindows()) return;
        VaultFixture.Demo().Write(DataPath, "correct horse");
        File.WriteAllText(StorePath, new JsonObject
        {
            ["userId"] = VaultFixture.UserId,
            ["challenge"] = Convert.ToBase64String(new byte[32]),
            ["key"] = Convert.ToBase64String(new byte[60]),
        }.ToJsonString());

        using var vault = Restarted(new FakeDevice());
        Assert.Equal(QuickUnlockStatus.Unavailable, (await vault.QuickUnlockAsync()).Status);
        Assert.False(File.Exists(StorePath));
        Assert.Equal(QuickUnlockSetting.NotSetUp, vault.QuickUnlockSetting);
    }
}
