using System.Text.Json.Nodes;
using Vaultbar.Platform;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

/// <summary>VBR-28: the low findings of the 2026-11-14 security review.</summary>
public class LowFindingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));

    public LowFindingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void L1_KeysMoveToMemoryTheCollectorNeverMoves_AndTheIncomingCopyIsZeroed()
    {
        var enc = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var mac = Enumerable.Range(33, 32).Select(i => (byte)i).ToArray();
        var expected = enc.Concat(mac).ToArray();

        using var key = new SymmetricKey(enc, mac);

        Assert.All(enc, b => Assert.Equal(0, b));
        Assert.All(mac, b => Assert.Equal(0, b));
        Assert.Equal(expected, key.ToBytes());
        // Arrays on the pinned object heap report the highest generation and are never compacted.
        Assert.Equal(GC.MaxGeneration, GC.GetGeneration(key.Enc));
    }

    [Fact]
    public void L3_RepromptAsksForTheMasterPasswordByDefault()
    {
        Assert.False(new Config().RepromptWithQuickUnlock);
    }

    [Fact]
    public void L4_EachAccountHasItsOwnWindowsHelloKey()
    {
        var a = WindowsHello.KeyName("6e1e5b8a-0000-4000-8000-000000000001");
        var b = WindowsHello.KeyName("0f9e8d7c-0000-4000-8000-000000000002");

        Assert.NotEqual(a, b);
        Assert.StartsWith("vaultbar-", a);
        Assert.NotEqual("vaultbar", a);
        Assert.Equal(a, WindowsHello.KeyName("6e1e5b8a-0000-4000-8000-000000000001"));
    }

    [Fact]
    public void L5_ThePipeNameHasARandomPartThatEveryLaunchReads()
    {
        var file = Path.Combine(_dir, "pipe-name");

        var first = Ipc.PipeSecret(file);
        Assert.Matches("^[0-9a-f]{32}$", first);
        Assert.Equal(first, Ipc.PipeSecret(file));
        Assert.NotEqual(first, Ipc.PipeSecret(Path.Combine(_dir, "other-user")));

        File.WriteAllText(file, "damaged");
        Assert.Matches("^[0-9a-f]{32}$", Ipc.PipeSecret(file));
    }

    [Theory]
    [InlineData(null, null, null, "https://icons.bitwarden.net")]
    [InlineData("EU", null, null, "https://icons.bitwarden.eu")]
    [InlineData(null, "https://icons.bitwarden.eu", null, "https://icons.bitwarden.eu")]
    [InlineData(null, null, "https://vault.contoso.com", "https://vault.contoso.com/icons")]
    [InlineData(null, "https://vault.contoso.com/icons/", "https://vault.contoso.com", "https://vault.contoso.com/icons")]
    [InlineData(null, "https://collector.fabrikam.example", null, "https://icons.bitwarden.net")]
    [InlineData(null, "https://collector.fabrikam.example", "https://vault.contoso.com", "https://vault.contoso.com/icons")]
    [InlineData(null, "http://icons.bitwarden.net", null, "https://icons.bitwarden.net")]
    public void L7_IconsComeOnlyFromBitwardenOrTheAccountsOwnServer(string? region, string? icons, string? server, string expected)
    {
        var urls = new JsonObject();
        if (icons != null) urls["icons"] = icons;
        if (server != null) urls["base"] = server;
        var env = new JsonObject { ["urls"] = urls };
        if (region != null) env["region"] = region;

        Assert.Equal(expected, DesktopStore.IconsUrl(env));
    }
}
