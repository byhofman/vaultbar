using System.Text;
using Vaultbar.Platform;
using Vaultbar.Vault;
using Xunit;

namespace Vaultbar.Tests;

public class AutoTyperTests
{
    private sealed class RecordingKeyboard : IKeyboard
    {
        public readonly StringBuilder Typed = new();
        public Action? AfterKey;

        public void Type(Rune character)
        {
            Typed.Append(character.ToString());
            AfterKey?.Invoke();
        }

        public void Press(SpecialKey key)
        {
            Typed.Append(key == SpecialKey.Tab ? "<TAB>" : "<ENTER>");
            AfterKey?.Invoke();
        }
    }

    private static List<TypeStep> Login() => AutoTypeSequence.Build("{USERNAME}{TAB}{PASSWORD}{ENTER}", "alex", "s3cret", () => null);

    [Fact]
    public void TypesEverythingWhileTheTargetKeepsFocus()
    {
        var keyboard = new RecordingKeyboard();
        Assert.Equal(AutoTypeResult.Typed, AutoTyper.Run(Login(), keyboard, () => true, _ => { }));
        Assert.Equal("alex<TAB>s3cret<ENTER>", keyboard.Typed.ToString());
    }

    [Fact]
    public void StopsAtTheFirstKeystrokeAfterFocusMoves()
    {
        var keyboard = new RecordingKeyboard();
        var focused = true;
        keyboard.AfterKey = () => focused = keyboard.Typed.ToString() != "alex<TAB>";

        Assert.Equal(AutoTypeResult.Interrupted, AutoTyper.Run(Login(), keyboard, () => focused, _ => { }));
        Assert.Equal("alex<TAB>", keyboard.Typed.ToString());
    }

    [Fact]
    public void TypesNothingWithoutFocus()
    {
        var keyboard = new RecordingKeyboard();
        Assert.Equal(AutoTypeResult.Interrupted, AutoTyper.Run(Login(), keyboard, () => false, _ => { }));
        Assert.Equal("", keyboard.Typed.ToString());
    }

    [Fact]
    public void LineBreaksAndTabsInsideTextBecomeKeys()
    {
        var keyboard = new RecordingKeyboard();
        AutoTyper.Run([new TypeText("a\r\nb\tc€😀")], keyboard, () => true, _ => { });
        Assert.Equal("a<ENTER>b<TAB>c€😀", keyboard.Typed.ToString());
    }

    [Fact]
    public void DelaysAreWaitedOut()
    {
        var waits = new List<int>();
        AutoTyper.Run([new TypeDelay(300)], new RecordingKeyboard(), () => true, waits.Add);
        Assert.Contains(300, waits);
    }
}

public class SecretClipboardTests
{
    private sealed class FakeClipboard : IClipboardAccess
    {
        private int _sequence;
        public string? Text;
        public int Clears;

        public Task<object?> SetClipboardAsync(string text)
        {
            Text = text;
            return Task.FromResult<object?>(++_sequence);
        }

        public Task ClearClipboardAsync(object token)
        {
            if ((int)token != _sequence) return Task.CompletedTask;
            Clears++;
            Text = null;
            return Task.CompletedTask;
        }

        public void UserCopies(string text)
        {
            _sequence++;
            Text = text;
        }
    }

    [Fact]
    public async Task ASecretIsClearedAfterTheConfiguredTime()
    {
        var board = new FakeClipboard();
        var clipboard = new SecretClipboard(board, 1);
        var copy = clipboard.CopyAsync("s3cret", sensitive: true);
        Assert.Equal("s3cret", board.Text);
        await copy.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(board.Text);
        Assert.False(clipboard.HoldsSecret);
    }

    [Fact]
    public async Task SomethingTheUserCopiedSinceIsLeftAlone()
    {
        var board = new FakeClipboard();
        var clipboard = new SecretClipboard(board, 30);
        _ = clipboard.CopyAsync("s3cret", sensitive: true);
        board.UserCopies("their own text");
        await clipboard.ClearNowAsync();
        Assert.Equal("their own text", board.Text);
    }

    [Fact]
    public async Task LockingClearsASecretEvenWithTheTimerOff()
    {
        var board = new FakeClipboard();
        var clipboard = new SecretClipboard(board, 0);
        await clipboard.CopyAsync("s3cret", sensitive: true);
        Assert.True(clipboard.HoldsSecret);
        await clipboard.ClearNowAsync();
        Assert.Null(board.Text);
    }

    [Fact]
    public async Task AUsernameIsNotCleared()
    {
        var board = new FakeClipboard();
        var clipboard = new SecretClipboard(board, 30);
        await clipboard.CopyAsync("alex", sensitive: false);
        await clipboard.ClearNowAsync();
        Assert.Equal("alex", board.Text);
        Assert.Equal(0, board.Clears);
    }

    [Fact]
    public void AFingerprintRecognisesTheTextWithoutKeepingIt()
    {
        var fingerprint = new ClipboardFingerprint("s3cret");
        Assert.True(fingerprint.Matches("s3cret"));
        Assert.False(fingerprint.Matches("s3cret "));
        Assert.False(fingerprint.Matches(null));
    }
}

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

public sealed class LocalFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vaultbar-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void PrivateFoldersAreOnlyOpenToTheUser()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(_dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Paths.EnsurePrivate(_dir);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_dir));

        var nested = Path.Combine(_dir, "a", "b");
        Paths.EnsurePrivate(nested);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(nested));
    }
}

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
