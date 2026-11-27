using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public sealed class AccessDeniedTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void AFileTheOsRefusesToOpen_IsReportedAsAccessDenied_NotAsUnreadable()
    {
        // Root and Windows ACLs are not stopped by file modes.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "data.json");
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.None);

        using var vault = new VaultService(new Config { DataPath = path });
        vault.Probe();

        Assert.Equal(VaultState.Error, vault.State);
        Assert.Equal(VaultError.AccessDenied, vault.Error);
    }

    [Fact]
    public void TheLogNamesTheKindOfFolder_NeverThePath()
    {
        var sep = Path.DirectorySeparatorChar;
        var container = string.Join(sep, "", "Users", "someone", "Library", "Containers", "com.bitwarden.desktop", "data.json");
        Assert.Equal("sandbox container", DesktopStore.Describe(container));
        Assert.DoesNotContain("someone", DesktopStore.Describe(container));
        Assert.Equal("no data file", DesktopStore.Describe(null));
    }
}
