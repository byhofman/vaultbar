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
    [InlineData("0", null, true, false, false)]
    [InlineData("onIdle", null, false, true, false)]
    [InlineData("-4", null, false, true, false)]
    [InlineData("onSleep", null, false, false, true)]
    [InlineData("-3", null, false, false, true)]
    [InlineData("onLocked", null, false, false, true)]
    [InlineData("-2", null, false, false, true)]
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
    public void OnLockedAndImmediately_HaveNoIdleLock()
    {
        Assert.False(LockPolicy.From(null, "onLocked").IdleExpired(TimeSpan.FromDays(3), TimeSpan.FromDays(3)));
        Assert.False(LockPolicy.From(null, "0").IdleExpired(TimeSpan.FromDays(3), TimeSpan.FromDays(3)));
    }
}
