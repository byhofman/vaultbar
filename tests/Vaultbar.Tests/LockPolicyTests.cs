using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

/// <summary>VBR-27 (M4): every vault timeout the Bitwarden desktop app stores, and what Vaultbar does with it.</summary>
public class LockPolicyTests
{
    [Theory]
    [InlineData("15", 15, false, false, false)]
    [InlineData("1", 1, false, false, false)]
    [InlineData("480", 480, false, false, false)]
    [InlineData("0", 15, true, false, false)]
    [InlineData("onIdle", null, false, true, false)]
    [InlineData("-4", null, false, true, false)]
    [InlineData("onSleep", 15, false, false, true)]
    [InlineData("-3", 15, false, false, true)]
    [InlineData("onLocked", 15, false, false, true)]
    [InlineData("-2", 15, false, false, true)]
    [InlineData("onRestart", 15, false, false, false)]
    [InlineData("-1", 15, false, false, false)]
    [InlineData("never", 15, false, false, false)]
    [InlineData("custom", 15, false, false, false)]
    [InlineData("-100", 15, false, false, false)]
    [InlineData(null, 15, false, false, false)]
    [InlineData("garbage", 15, false, false, false)]
    public void BitwardenTimeout_MapsToALock(string? timeout, int? idleMinutes, bool onHide, bool onSystemIdle, bool onSystemLock)
    {
        var policy = LockPolicy.From(null, timeout);

        Assert.Equal(idleMinutes is { } m ? TimeSpan.FromMinutes(m) : null, policy.IdleAfter);
        Assert.Equal(onHide, policy.OnBarHidden);
        Assert.Equal(onSystemIdle, policy.OnSystemIdle);
        Assert.Equal(onSystemLock, policy.OnSystemLock);
    }

    [Theory]
    [InlineData(5, "never", 5)]
    [InlineData(30, "0", 30)]
    [InlineData(0, "15", null)]
    public void LockAfterMinutes_OverridesBitwarden(int minutes, string timeout, int? expected)
    {
        var policy = LockPolicy.From(minutes, timeout);

        Assert.Equal(expected is { } m ? TimeSpan.FromMinutes(m) : null, policy.IdleAfter);
        Assert.False(policy.OnBarHidden || policy.OnSystemIdle || policy.OnSystemLock);
    }

    [Fact]
    public void IdleMinutes_CountFromTheLastUseOfTheBar()
    {
        var policy = LockPolicy.From(null, "15");

        Assert.False(policy.IdleExpired(TimeSpan.FromMinutes(14), systemIdle: null));
        Assert.True(policy.IdleExpired(TimeSpan.FromMinutes(15), systemIdle: TimeSpan.Zero));
    }

    [Fact]
    public void OnSystemIdle_LocksAfterFiveMinutesWithoutInput()
    {
        var policy = LockPolicy.From(null, "onIdle");

        Assert.False(policy.IdleExpired(TimeSpan.FromHours(2), TimeSpan.FromMinutes(4)));
        Assert.True(policy.IdleExpired(TimeSpan.FromHours(2), TimeSpan.FromMinutes(5)));
        Assert.True(policy.IdleExpired(TimeSpan.FromMinutes(5), systemIdle: null));
        Assert.False(policy.IdleExpired(TimeSpan.FromMinutes(4), systemIdle: null));
    }

    [Fact]
    public void OnLockedAndImmediately_KeepTheIdleFallbackAsABackstop()
    {
        Assert.False(LockPolicy.From(null, "onLocked").IdleExpired(TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(14)));
        Assert.True(LockPolicy.From(null, "onLocked").IdleExpired(TimeSpan.FromMinutes(15), TimeSpan.Zero));
        Assert.True(LockPolicy.From(null, "0").IdleExpired(TimeSpan.FromMinutes(15), TimeSpan.Zero));
    }

    [Theory]
    [InlineData("\"onIdle\"", "onIdle")]
    [InlineData("-2", "-2")]
    [InlineData("0", "0")]
    [InlineData("null", null)]
    public async Task TheDesktopAppsStoredValue_IsReadAsItIs(string json, string? expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "data.json");
            new VaultFixture { VaultTimeoutJson = json }.Write(path, "correct horse");
            using var vault = new VaultService(new Config { DataPath = path });
            vault.Probe();
            Assert.Null(await vault.UnlockAsync("correct horse"));
            Assert.Equal(LockPolicy.From(null, expected), vault.LockPolicy);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("15", false)]
    [InlineData("\"onLocked\"", false)]
    public async Task ClosingTheBar_LocksOnlyUnderImmediately(string json, bool locks)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "data.json");
            new VaultFixture { VaultTimeoutJson = json }.Write(path, "correct horse");
            using var vault = new VaultService(new Config { DataPath = path });
            vault.Probe();
            Assert.Null(await vault.UnlockAsync("correct horse"));

            Assert.Equal(locks, vault.LockBecauseTheBarClosed());
            Assert.Equal(locks ? VaultState.Locked : VaultState.Unlocked, vault.State);
            Assert.False(vault.LockBecauseTheBarClosed());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
