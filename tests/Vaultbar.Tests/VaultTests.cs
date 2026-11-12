using Vaultbar.Platform;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public class VaultTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_dir, "data.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private async Task<VaultService> UnlockedDemo()
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var vault = new VaultService(new Config { DataPath = DataPath });
        vault.Probe();
        Assert.Equal(VaultState.Locked, vault.State);
        Assert.Null(await vault.UnlockAsync("correct horse"));
        return vault;
    }

    [Fact]
    public async Task WrongPassword_IsRejected()
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var vault = new VaultService(new Config { DataPath = DataPath });
        vault.Probe();
        Assert.Equal(VaultFixture.Email, vault.Email);
        Assert.Equal("Invalid master password.", await vault.UnlockAsync("wrong"));
        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public async Task Unlock_DecryptsItemsIncludingItemKeysAndSkipsDeleted()
    {
        using var vault = await UnlockedDemo();
        Assert.Equal(VaultState.Unlocked, vault.State);
        Assert.Equal(0, vault.FailedItems);
        Assert.Equal(16, vault.Items.Count);
        Assert.DoesNotContain(vault.Items, i => i.Name == "Old account");

        var azure = vault.Items.Single(i => i.Name == "Azure Portal");
        Assert.Equal("Azure#2026", azure.Password);
        Assert.Equal("Work", azure.Folder);
        Assert.Equal(["portal.azure.com"], azure.Hosts);

        var card = vault.Items.Single(i => i.Type == ItemType.Card);
        Assert.Equal("Visa, *1111", card.Subtitle);
        Assert.Equal("123", card.SecondaryCopy!.Value());
    }

    [Fact]
    public async Task Lock_ClearsItems_AndVerifyChecksMasterPassword()
    {
        var vault = await UnlockedDemo();
        Assert.True(await vault.VerifyMasterPasswordAsync("correct horse"));
        Assert.False(await vault.VerifyMasterPasswordAsync("nope"));
        vault.Lock();
        Assert.Equal(VaultState.Locked, vault.State);
        Assert.Empty(vault.Items);
    }

    [Fact]
    public async Task IdleTimeout_FollowsDesktopSettingUnlessOverridden()
    {
        using var vault = await UnlockedDemo();
        Assert.Equal(TimeSpan.FromMinutes(15), vault.IdleTimeout);
    }

    [Fact]
    public async Task Search_RanksPrefixAndHostMatches()
    {
        using var vault = await UnlockedDemo();
        var usage = new UsageStore();
        Assert.Equal("Azure DevOps", Matcher.Search(vault.Items, "azure dev", usage).First().Name);
        Assert.Equal("GitHub", Matcher.Search(vault.Items, "gh", usage).First().Name);
        Assert.Contains(Matcher.Search(vault.Items, "docker", usage), i => i.Name == "Docker Hub");
        Assert.Empty(Matcher.Search(vault.Items, "zzzz", usage));
    }

    [Fact]
    public async Task Suggest_MatchesBrowserTitleAgainstItemDomains()
    {
        using var vault = await UnlockedDemo();
        var window = new ForeignWindow(0, "Sign in to GitHub · GitHub - Google Chrome", "chrome");
        Assert.Equal("GitHub", Matcher.Suggest(vault.Items, window, new UsageStore()).First().Name);
        var bank = new ForeignWindow(0, "Log in | Bank of Example - Mozilla Firefox", "firefox");
        Assert.Contains(Matcher.Suggest(vault.Items, bank, new UsageStore()), i => i.Name == "Bank of Example");
    }

    [Theory]
    [InlineData("Pull requests · contoso/app - Google Chrome", "chrome")]
    [InlineData("Dashboard - Work - Microsoft​ Edge", "msedge")]
    [InlineData("New Tab - Google Chrome", "chrome")]
    public async Task Suggest_IgnoresBrowserBranding(string title, string app)
    {
        using var vault = await UnlockedDemo();
        Assert.DoesNotContain(Matcher.Suggest(vault.Items, new ForeignWindow(0, title, app), new UsageStore()), i => i.Name is "Gmail" or "Microsoft 365");
    }

    [Fact]
    public async Task Suggest_KeepsSiteNameBeforeBrowserSuffix()
    {
        using var vault = await UnlockedDemo();
        var window = new ForeignWindow(0, "Sign in – Google Accounts - Google Chrome", "chrome");
        Assert.Contains(Matcher.Suggest(vault.Items, window, new UsageStore()), i => i.Name == "Gmail");
    }

    [Theory]
    [InlineData(1_000, null, null, KdfType.Pbkdf2)]
    [InlineData(5_000_000, null, null, KdfType.Pbkdf2)]
    [InlineData(1, 64, 4, KdfType.Argon2id)]
    [InlineData(3, 1_000_000, 4, KdfType.Argon2id)]
    [InlineData(3, 64, 64, KdfType.Argon2id)]
    public void KdfOutsideBitwardenBounds_IsRejected(int iterations, int? memory, int? parallelism, KdfType type)
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(DataPath))!.AsObject();
        var unlock = root.Single(p => p.Key.EndsWith("masterPasswordUnlock_masterPasswordUnlockKey")).Value!;
        unlock["kdf"] = new System.Text.Json.Nodes.JsonObject
        {
            ["kdfType"] = (int)type, ["iterations"] = iterations, ["memory"] = memory, ["parallelism"] = parallelism,
        };
        File.WriteAllText(DataPath, root.ToJsonString());

        var vault = new VaultService(new Config { DataPath = DataPath });
        vault.Probe();
        Assert.Equal(VaultState.Error, vault.State);
        Assert.Contains("KDF", vault.Error);
    }

    [Fact]
    public async Task WebVaultUrl_DefaultsToBitwardenCom()
    {
        using var vault = await UnlockedDemo();
        Assert.Equal("https://vault.bitwarden.com", vault.WebVaultUrl);
    }
}
