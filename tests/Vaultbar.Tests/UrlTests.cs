using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public class UrlTests
{
    [Theory]
    [InlineData("github.com/login", "https://github.com/login")]
    [InlineData("http://intranet.example.org", "http://intranet.example.org/")]
    [InlineData("file:///C:/Windows/System32/calc.exe", null)]
    [InlineData(@"C:\Windows\System32\calc.exe", null)]
    [InlineData(@"\\server\share", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("androidapp://com.example", null)]
    [InlineData("", null)]
    public void WebUrl_OnlyAllowsHttp(string input, string? expected)
    {
        Assert.Equal(expected, WebAddress.From(input)?.Value);
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("portal.azure.com", true)]
    [InlineData("localhost", false)]
    [InlineData("nas", false)]
    [InlineData("printer.local", false)]
    [InlineData("router.lan", false)]
    [InlineData("jira.corp", false)]
    [InlineData("app.internal", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("10.0.0.5", false)]
    public void IsPublicHost_KeepsLocalNamesPrivate(string host, bool expected)
    {
        Assert.Equal(expected, VaultItem.IsPublicHost(host));
    }
}
