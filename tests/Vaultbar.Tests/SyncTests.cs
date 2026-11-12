using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public sealed class SyncTests : IDisposable
{
    private const string Password = "correct horse";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_dir, "data.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private async Task<VaultService> Unlock(VaultFixture fixture)
    {
        fixture.Write(DataPath, Password);
        var vault = new VaultService(new Config { DataPath = DataPath });
        vault.Probe();
        Assert.Null(await vault.UnlockAsync(Password));
        return vault;
    }

    private static async Task SyncAsync(VaultService vault, Action write)
    {
        var changed = new TaskCompletionSource();
        vault.Changed += () => changed.TrySetResult();
        write();
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ItemsFromBeforeASyncStillOpen()
    {
        var fixture = new VaultFixture();
        var org = fixture.AddOrganization();
        fixture.AddOrgLogin(org, "Team wiki", "t", "wiki-pass", itemKey: true);
        fixture.AddLogin("Contoso", "alex", "contoso-pass", itemKey: true);
        using var vault = await Unlock(fixture);
        var before = vault.Items;

        fixture.AddLogin("Added by sync", "s", "sync-pass");
        await SyncAsync(vault, () => fixture.Write(DataPath, Password));

        Assert.Equal(3, vault.Items.Count);
        Assert.Equal("wiki-pass", before.Single(i => i.Name == "Team wiki").Password);
        Assert.Equal("contoso-pass", before.Single(i => i.Name == "Contoso").Password);
        Assert.Equal("sync-pass", vault.Items.Single(i => i.Name == "Added by sync").Password);
    }

    [Fact]
    public async Task SwitchingAccountsInTheDesktopAppLocks()
    {
        using var vault = await Unlock(VaultFixture.Demo());
        var otherAccount = File.ReadAllText(DataPath).Replace(VaultFixture.UserId, "9a466c6e-0000-4000-8000-000000000002");
        await SyncAsync(vault, () => File.WriteAllText(DataPath, otherAccount));

        Assert.Equal(VaultState.Locked, vault.State);
        Assert.Empty(vault.Items);
    }
}
