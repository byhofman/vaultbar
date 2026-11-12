using Xunit;

namespace Vaultbar.Tests;

public class AutostartTests
{
    [Theory]
    [InlineData("/home/alex/.local/bin/vaultbar", "\"/home/alex/.local/bin/vaultbar\"")]
    [InlineData("/home/a b/$x\"y\\z%", "\"/home/a b/\\\\$x\\\\\"y\\\\\\\\z%%\"")]
    public void DesktopEntryQuotesThePath(string path, string exec)
    {
        Assert.Equal(exec, Setup.DesktopExec(path));
    }
}
