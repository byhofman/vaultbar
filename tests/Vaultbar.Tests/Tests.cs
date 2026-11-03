using System.Security.Cryptography;
using Vaultbar.Platform;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public class TotpTests
{
    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(2000000000, "69279037")]
    public void Rfc6238_Sha1(long time, string expected)
    {
        var code = Totp.Generate("otpauth://totp/x?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&digits=8", DateTimeOffset.FromUnixTimeSeconds(time));
        Assert.Equal(expected, code.Value);
    }

    [Fact]
    public void Rfc6238_Sha256()
    {
        var code = Totp.Generate("otpauth://totp/x?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQGEZA&digits=8&algorithm=SHA256",
            DateTimeOffset.FromUnixTimeSeconds(59));
        Assert.Equal("46119246", code.Value);
    }

    [Fact]
    public void PlainSecret_DefaultsToSixDigitsAnd30s()
    {
        var code = Totp.Generate("gezd gnbv gy3t qojq gezd gnbv gy3t qojq", DateTimeOffset.FromUnixTimeSeconds(59));
        Assert.Equal("287082", code.Value);
        Assert.Equal(1, code.SecondsRemaining);
    }

    [Fact]
    public void Steam_UsesFiveCharAlphabet()
    {
        var code = Totp.Generate("steam://GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", DateTimeOffset.FromUnixTimeSeconds(59));
        Assert.Equal(5, code.Value.Length);
        Assert.All(code.Value, c => Assert.Contains(c, "23456789BCDFGHJKMNPQRTVWXY"));
    }
}

public class CryptoTests
{
    [Fact]
    public void EncString_RoundTrips()
    {
        using var key = SymmetricKey.FromBytes(RandomNumberGenerator.GetBytes(64));
        var enc = EncString.Encrypt("héllo wörld 🔐", key);
        Assert.StartsWith("2.", enc);
        Assert.Equal("héllo wörld 🔐", EncString.Decrypt(enc, key));
    }

    [Fact]
    public void EncString_RejectsTamperedMac()
    {
        using var key = SymmetricKey.FromBytes(RandomNumberGenerator.GetBytes(64));
        var parts = EncString.Encrypt("secret", key).Split('|');
        var mac = Convert.FromBase64String(parts[2]);
        mac[0] ^= 1;
        Assert.Throws<CryptographicException>(() => EncString.Decrypt($"{parts[0]}|{parts[1]}|{Convert.ToBase64String(mac)}", key));
    }

    [Fact]
    public void Pbkdf2MasterKey_MatchesReference()
    {
        var key = MasterKey.Derive("asdfasdf", "test@example.com", new KdfConfig(KdfType.Pbkdf2, 100000, null, null));
        var reference = Rfc2898DeriveBytes.Pbkdf2("asdfasdf"u8.ToArray(), "test@example.com"u8.ToArray(), 100000, HashAlgorithmName.SHA256, 32);
        Assert.Equal(reference, key);
    }

    [Fact]
    public void Argon2MasterKey_IsDeterministic()
    {
        var kdf = new KdfConfig(KdfType.Argon2id, 2, 16, 2);
        Assert.Equal(MasterKey.Derive("pw", "salt@example.com", kdf), MasterKey.Derive("pw", "salt@example.com", kdf));
        Assert.Equal(32, MasterKey.Derive("pw", "salt@example.com", kdf).Length);
    }
}

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
}

public class SequenceTests
{
    [Fact]
    public void DefaultSequence_TypesUserTabPassword()
    {
        var steps = AutoTypeSequence.Build("{USERNAME}{TAB}{PASSWORD}", "alex", "pw", () => null);
        Assert.Equal<TypeStep>([new TypeText("alex"), new TypeKey(SpecialKey.Tab), new TypeText("pw")], steps);
    }

    [Fact]
    public void MissingUsername_DropsFollowingTab()
    {
        var steps = AutoTypeSequence.Build("{USERNAME}{TAB}{PASSWORD}{ENTER}", "", "pw", () => null);
        Assert.Equal<TypeStep>([new TypeText("pw"), new TypeKey(SpecialKey.Enter)], steps);
    }

    [Fact]
    public void Tokens_AreCaseInsensitiveAndSupportDelayAndTotp()
    {
        var steps = AutoTypeSequence.Build("{password}{delay:500}{TOTP}", "u", "pw", () => "123456");
        Assert.Equal<TypeStep>([new TypeText("pw"), new TypeDelay(500), new TypeText("123456")], steps);
    }

    [Theory]
    [InlineData("Ctrl+Shift+Space", true, true, false, false, "Space")]
    [InlineData("cmd+shift+space", false, true, false, true, "Space")]
    [InlineData("Ctrl+Alt+k", true, false, true, false, "K")]
    [InlineData("Super+F12", false, false, false, true, "F12")]
    public void Hotkey_Parses(string text, bool ctrl, bool shift, bool alt, bool super, string key)
    {
        Assert.Equal(new Hotkey(ctrl, shift, alt, super, key), Hotkey.Parse(text));
    }

    [Fact]
    public void Hotkey_RejectsGarbage()
    {
        Assert.Throws<FormatException>(() => Hotkey.Parse("Ctrl+Shift"));
        Assert.Throws<FormatException>(() => Hotkey.Parse("Ctrl+PrintScreenPlease"));
    }

    [Fact]
    public void MainLabel_HandlesSecondLevelSuffixes()
    {
        Assert.Equal("bankofexample", Matcher.MainLabel("bankofexample.co.uk"));
        Assert.Equal("azure", Matcher.MainLabel("portal.azure.com"));
        Assert.Equal("github", Matcher.MainLabel("github.com"));
    }
}

public class DemoVault
{
    [Fact]
    public void WriteDemoVaultWhenRequested()
    {
        var target = Environment.GetEnvironmentVariable("VAULTBAR_DEMO_OUT");
        if (string.IsNullOrEmpty(target)) return;
        VaultFixture.Demo().Write(target, "demo-password");
    }
}
