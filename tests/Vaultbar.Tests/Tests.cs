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

public class QuickUnlockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_dir, "data.json");
    private string StorePath => Path.Combine(_dir, "quick-unlock.dat");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class FakeDevice : IQuickUnlockProvider
    {
        public byte[] Secret = RandomNumberGenerator.GetBytes(32);
        public QuickUnlockStatus Next = QuickUnlockStatus.Success;
        public int Prompts;
        public bool Deleted;

        public string Name => "Fake Hello";
        public Task<bool> IsAvailableAsync() => Task.FromResult(true);

        public Task<(QuickUnlockStatus Status, byte[]? Signature)> SignAsync(byte[] challenge, bool createKey)
        {
            Prompts++;
            if (createKey) Secret = RandomNumberGenerator.GetBytes(32);
            return Task.FromResult<(QuickUnlockStatus, byte[]?)>(Next == QuickUnlockStatus.Success
                ? (Next, HMACSHA256.HashData(Secret, challenge))
                : (Next, null));
        }

        public Task DeleteKeyAsync()
        {
            Deleted = true;
            return Task.CompletedTask;
        }
    }

    private async Task<(VaultService Vault, FakeDevice Device)> Enrolled()
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var device = new FakeDevice();
        var vault = new VaultService(new Config { DataPath = DataPath }, StorePath) { QuickUnlock = device };
        vault.Probe();
        Assert.Equal(QuickUnlockSetting.NotSetUp, vault.QuickUnlockSetting);
        Assert.Null(await vault.UnlockAsync("correct horse"));
        Assert.True(await vault.ShouldOfferQuickUnlockAsync());
        Assert.Equal(QuickUnlockStatus.Success, await vault.SetUpQuickUnlockAsync());
        Assert.True(vault.CanQuickUnlock);
        return (vault, device);
    }

    [Fact]
    public async Task SetUp_ThenUnlockWithDevice_DecryptsVault()
    {
        var (vault, _) = await Enrolled();
        vault.Lock();
        var (status, error) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Success, status);
        Assert.Null(error);
        Assert.Equal(VaultState.Unlocked, vault.State);
        Assert.Equal("Azure#2026", vault.Items.Single(i => i.Name == "Azure Portal").Password);
    }

    [Fact]
    public async Task AfterDeviceUnlock_MasterPasswordAndDeviceStillVerify()
    {
        var (vault, _) = await Enrolled();
        vault.Lock();
        await vault.QuickUnlockAsync();
        Assert.True(await vault.VerifyMasterPasswordAsync("correct horse"));
        Assert.False(await vault.VerifyMasterPasswordAsync("nope"));
        Assert.Equal(QuickUnlockStatus.Success, await vault.VerifyWithQuickUnlockAsync());
    }

    [Fact]
    public async Task WrongDeviceKey_FailsAndForgetsEnrollment()
    {
        var (vault, device) = await Enrolled();
        vault.Lock();
        device.Secret = RandomNumberGenerator.GetBytes(32);
        var (status, error) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Failed, status);
        Assert.NotNull(error);
        Assert.Equal(VaultState.Locked, vault.State);
        Assert.Equal(QuickUnlockSetting.NotSetUp, vault.QuickUnlockSetting);
    }

    [Fact]
    public async Task StoredKeyFromAnotherVault_IsRejected()
    {
        var (vault, _) = await Enrolled();
        vault.Lock();
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Failed, status);
        Assert.Equal(VaultState.Locked, vault.State);
    }

    [Fact]
    public async Task CanceledSetUp_IsRemembered_AndCanBeTurnedBackOn()
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var device = new FakeDevice { Next = QuickUnlockStatus.Canceled };
        var vault = new VaultService(new Config { DataPath = DataPath }, StorePath) { QuickUnlock = device };
        vault.Probe();
        await vault.UnlockAsync("correct horse");
        Assert.Equal(QuickUnlockStatus.Canceled, await vault.SetUpQuickUnlockAsync());
        Assert.Equal(QuickUnlockSetting.Off, vault.QuickUnlockSetting);
        Assert.False(await vault.ShouldOfferQuickUnlockAsync());

        device.Next = QuickUnlockStatus.Success;
        await vault.SetQuickUnlockEnabledAsync(true);
        Assert.Equal(QuickUnlockSetting.On, vault.QuickUnlockSetting);

        await vault.SetQuickUnlockEnabledAsync(false);
        Assert.Equal(QuickUnlockSetting.Off, vault.QuickUnlockSetting);
        Assert.True(device.Deleted);
    }
}

public sealed class TouchIdTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));
    private string DataPath => Path.Combine(_dir, "data.json");
    private string KeyPath => Path.Combine(_dir, "touch-id-key.dat");
    private string StorePath => Path.Combine(_dir, "quick-unlock.dat");
    private readonly FakeEnclave _enclave = new();

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class FakeEnclave : ITouchIdDevice
    {
        public bool Available { get; set; } = true;
        public int NextCreate;
        public int NextDerive;
        public int Prompts;

        public int Create(out byte[] blob)
        {
            blob = NextCreate == 0 ? RandomNumberGenerator.GetBytes(64) : [];
            return NextCreate;
        }

        public int Derive(byte[] blob, byte[] challenge, string reason, out byte[] secret)
        {
            Prompts++;
            secret = NextDerive == 0 ? HMACSHA256.HashData(blob, challenge) : [];
            return NextDerive;
        }
    }

    private async Task<VaultService> Enrolled()
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        var vault = new VaultService(new Config { DataPath = DataPath }, StorePath) { QuickUnlock = new MacTouchId(_enclave, KeyPath) };
        vault.Probe();
        Assert.Null(await vault.UnlockAsync("correct horse"));
        Assert.Equal(QuickUnlockStatus.Success, await vault.SetUpQuickUnlockAsync());
        vault.Lock();
        return vault;
    }

    [Fact]
    public async Task SetUp_ThenTouch_UnlocksTheVault()
    {
        var vault = await Enrolled();
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Success, status);
        Assert.Equal("Azure#2026", vault.Items.Single(i => i.Name == "Azure Portal").Password);
    }

    [Fact]
    public async Task SetUp_AsksForTouchSoABrokenSensorShowsAtOnce()
    {
        await Enrolled();
        Assert.Equal(1, _enclave.Prompts);
    }

    [Fact]
    public async Task KeyAndStoreFiles_AreOnlyReadableByTheUser()
    {
        if (OperatingSystem.IsWindows()) return;
        await Enrolled();
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Assert.Equal(ownerOnly, File.GetUnixFileMode(KeyPath));
        Assert.Equal(ownerOnly, File.GetUnixFileMode(StorePath));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-4)]
    [InlineData(-128)]
    public async Task Canceling_KeepsTheEnrollment(int code)
    {
        var vault = await Enrolled();
        _enclave.NextDerive = code;
        var (status, error) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Canceled, status);
        Assert.Null(error);
        Assert.Equal(VaultState.Locked, vault.State);
        Assert.True(vault.CanQuickUnlock);
    }

    [Fact]
    public async Task ClosedLid_KeepsTheEnrollmentAndDoesNotAsk()
    {
        var vault = await Enrolled();
        var prompts = _enclave.Prompts;
        _enclave.Available = false;
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Canceled, status);
        Assert.Equal(prompts, _enclave.Prompts);
        Assert.True(vault.CanQuickUnlock);
    }

    [Theory]
    [InlineData(-25300)]
    [InlineData(-25293)]
    public async Task ChangedFingers_ForgetTheEnrollmentSoThePasswordCanSetItUpAgain(int code)
    {
        var vault = await Enrolled();
        _enclave.NextDerive = code;
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Unavailable, status);
        Assert.Equal(VaultState.Locked, vault.State);

        _enclave.NextDerive = 0;
        Assert.Null(await vault.UnlockAsync("correct horse"));
        Assert.True(await vault.ShouldOfferQuickUnlockAsync());
    }

    [Fact]
    public async Task LockedOutSensor_FailsWithoutForgetting()
    {
        var vault = await Enrolled();
        _enclave.NextDerive = -8;
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Failed, status);
        Assert.True(vault.CanQuickUnlock);
    }

    [Fact]
    public async Task MissingKeyFile_ForgetsTheEnrollment()
    {
        var vault = await Enrolled();
        File.Delete(KeyPath);
        var (status, _) = await vault.QuickUnlockAsync();
        Assert.Equal(QuickUnlockStatus.Unavailable, status);
        Assert.False(vault.CanQuickUnlock);
    }

    [Fact]
    public async Task EnclaveRefusingToCreateAKey_DoesNotEnrollOrLeaveAFile()
    {
        VaultFixture.Demo().Write(DataPath, "correct horse");
        _enclave.NextCreate = -25308;
        var vault = new VaultService(new Config { DataPath = DataPath }, StorePath) { QuickUnlock = new MacTouchId(_enclave, KeyPath) };
        vault.Probe();
        await vault.UnlockAsync("correct horse");
        Assert.Equal(QuickUnlockStatus.Failed, await vault.SetUpQuickUnlockAsync());
        Assert.False(File.Exists(KeyPath));
        Assert.False(vault.CanQuickUnlock);
    }

    [Fact]
    public async Task TurningItOff_RemovesTheKeyFile()
    {
        var vault = await Enrolled();
        await vault.QuickUnlockAsync();
        await vault.SetQuickUnlockEnabledAsync(false);
        Assert.False(File.Exists(KeyPath));
        Assert.Equal(QuickUnlockSetting.Off, vault.QuickUnlockSetting);
    }
}

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
        Assert.Equal(expected, VaultItem.WebUrl(input));
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

public class MacPlatformTests
{
    [Fact]
    public void EveryDocumentedHotkeyKeyHasAKeyCode()
    {
        var keys = Enumerable.Range('A', 26).Concat(Enumerable.Range('0', 10)).Select(c => ((char)c).ToString())
            .Concat(Enumerable.Range(1, 20).Select(f => $"F{f}"))
            .Append("Space");
        Assert.All(keys, k => Assert.True(MacPlatform.KeyCodes.ContainsKey(k), k));
        Assert.Equal(MacPlatform.KeyCodes.Count, MacPlatform.KeyCodes.Values.Distinct().Count());
    }

    [Fact]
    public void DefaultHotkeyParses()
    {
        var hotkey = Hotkey.Parse("Cmd+Shift+Space");
        Assert.True(hotkey is { Super: true, Shift: true, Ctrl: false, Alt: false, Key: "Space" });
    }

    [Fact]
    public void MissingConfiguredVaultNeverFallsBackToTheStandardLocations()
    {
        Assert.Null(DesktopStore.Locate(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "data.json")));
    }

    [Fact]
    public void LooksInBothMacBitwardenLocations()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var paths = DesktopStore.CandidatePaths().ToList();
        Assert.Contains(paths, p => p.EndsWith("Library/Application Support/Bitwarden/data.json") && !p.Contains("Containers"));
        Assert.Contains(paths, p => p.EndsWith("Library/Containers/com.bitwarden.desktop/Data/Library/Application Support/Bitwarden/data.json"));
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
