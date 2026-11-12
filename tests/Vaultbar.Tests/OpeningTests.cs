using Vaultbar.Platform;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public class OpeningTests
{
    [Fact]
    public void TheWebVaultLinkIsBuiltOnlyFromAWebAddress()
    {
        Assert.Equal("https://vault.example.com/#/vault?itemId=a%26b&action=view",
            WebAddress.ForWebVaultItem("https://vault.example.com/", "a&b")?.Value);
        Assert.Null(WebAddress.ForWebVaultItem("file:///C:/Windows/System32/calc.exe", "x"));
        Assert.Null(WebAddress.ForWebVaultItem("calc.exe --flag", "x"));
    }

    [Theory]
    [InlineData("2", "/org/freedesktop/login1/session/_32")]
    [InlineData("c1", "/org/freedesktop/login1/session/c1")]
    [InlineData("12", "/org/freedesktop/login1/session/_312")]
    [InlineData("a-b", "/org/freedesktop/login1/session/a_2db")]
    [InlineData("", "/org/freedesktop/login1/session/_")]
    public void LogindSessionPathsAreEscapedLikeSystemd(string id, string path)
    {
        Assert.Equal(path, LinuxSessionLock.SessionPath(id));
    }
}
