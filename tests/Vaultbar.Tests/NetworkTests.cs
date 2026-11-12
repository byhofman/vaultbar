using System.Text.RegularExpressions;
using Xunit;

namespace Vaultbar.Tests;

/// <summary>
/// The README promises that the only network traffic is website icons. This keeps the promise checkable: any new code
/// that could reach the network has to show up here first.
/// </summary>
public partial class NetworkTests
{
    [GeneratedRegex(@"\b(HttpClient|HttpMessageHandler|WebRequest|WebClient|TcpClient|UdpClient|TcpListener|ClientWebSocket|Socket)\b|(?<!\.)\bDns\.|System\.Net\.(Sockets|WebSockets|Mail)")]
    private static partial Regex NetworkApi();

    [Fact]
    public void OnlyTheIconServiceTouchesTheNetwork()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "vaultbar.slnx"))) root = root.Parent;
        Assert.NotNull(root);

        var src = Path.Combine(root.FullName, "src", "Vaultbar");
        var users = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => NetworkApi().IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .ToList();

        Assert.Equal(["UI/FaviconService.cs"], users);
    }
}
