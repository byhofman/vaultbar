using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Vaultbar.Platform;
using Vaultbar.Tests;
using Vaultbar.UI;
using Vaultbar.Vault;
using N = Vaultbar.MacE2E.Native;

namespace Vaultbar.MacE2E;

internal sealed class RecordingPlatform(MacPlatform mac) : IPlatform
{
    public readonly List<string> Opened = [];
    public readonly FakeTouchIdDevice TouchIdDevice = new();
    public IQuickUnlockProvider? QuickUnlock { get; private set; }

    public void UseFakeTouchId(string keyPath) => QuickUnlock = new MacTouchId(TouchIdDevice, keyPath);

    public string? StartHotkey(Hotkey hotkey, Action onPressed) => mac.StartHotkey(hotkey, onPressed);
    public ForeignWindow? CaptureForeground() => mac.CaptureForeground();
    public PixelPoint? AnchorPoint(ForeignWindow? target) => mac.AnchorPoint(target);
    public void AttachWindow(Window window) => mac.AttachWindow(window);
    public void AfterStart(bool shown) => mac.AfterStart(shown);
    public void AfterShow() => mac.AfterShow();
    public void AfterHide() => mac.AfterHide();
    public void ExcludeFromCapture(Window window) => mac.ExcludeFromCapture(window);

    // MacPlatform would open the system Accessibility dialog here, which is the owner's to answer.
    public string? AutoTypeUnavailableReason(ForeignWindow? target) => "test run: Accessibility not requested";

    public Task<AutoTypeResult> AutoTypeAsync(ForeignWindow? target, IReadOnlyList<TypeStep> steps, Action hideWindow) => mac.AutoTypeAsync(target, steps, hideWindow);
    public Task<object?> SetClipboardAsync(string text) => mac.SetClipboardAsync(text);
    public Task ClearClipboardAsync(object token) => mac.ClearClipboardAsync(token);
    public void OpenUrl(WebAddress address) => Opened.Add(address.Value);
    public void WatchSystemLock(Action onLock) => mac.WatchSystemLock(onLock);
    public void Dispose() => mac.Dispose();
}

/// <summary>Stands in for the Secure Enclave and the finger, so the full unlock flow runs with no system dialog.</summary>
internal sealed class FakeTouchIdDevice : ITouchIdDevice
{
    public int NextResult;
    public int Prompts;

    public bool Available => true;

    public int Create(out byte[] blob)
    {
        blob = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return 0;
    }

    public int Derive(byte[] blob, byte[] challenge, string reason, out byte[] secret)
    {
        Prompts++;
        secret = NextResult == 0 ? System.Security.Cryptography.HMACSHA256.HashData(blob, challenge) : [];
        return NextResult;
    }
}

internal static class Program
{
    private const string Password = "e2e-master-password";
    private const string Board = "vaultbar-e2e-clipboard";

    private static readonly List<(string Name, bool Ok)> Results = [];
    private static readonly RecordingPlatform Platform = new(new MacPlatform(Board, N.FakeLockNotification));
    private static readonly VaultFixture _fixture = VaultFixture.Demo();
    private static string _vaultPath = "";
    private static string _shots = "";
    private static QuickWindow _window = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--target", var parent])
        {
            ForegroundTarget.Run(int.Parse(parent));
            return 0;
        }

        var sandbox = Environment.GetEnvironmentVariable("E2E_SANDBOX") ?? "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (sandbox.Length == 0 || home != sandbox || !Paths.ConfigDir.StartsWith(sandbox, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Refusing to run outside the throwaway HOME. Use tests/e2e-macos/run.sh.");
            return 2;
        }

        _shots = Environment.GetEnvironmentVariable("E2E_OUT") ?? sandbox;
        _vaultPath = Path.Combine(sandbox, "fake-vault", "data.json");
        _fixture.Write(_vaultPath, Password);
        Platform.UseFakeTouchId(Path.Combine(Paths.CacheDir, "touch-id-key.dat"));
        new QuickUnlockStore(Path.Combine(Paths.CacheDir, "quick-unlock.dat")).SaveDeclined(VaultFixture.UserId);
        Directory.CreateDirectory(Paths.ConfigDir);
        File.WriteAllText(Config.FilePath, $$"""
            { "dataPath": "{{_vaultPath}}", "showFavicons": false, "clipboardClearSeconds": 2, "hideAfterCopy": false, "lockAfterMinutes": 1 }
            """);

        System.Diagnostics.Process.Start(Environment.ProcessPath!, ["--target", Environment.ProcessId.ToString()]);
        Thread.Sleep(2500);
        App.ShowOnStart = false;
        App.PlatformFactory = () => Platform;
        Vaultbar.Program.BuildAvaloniaApp()
            .AfterSetup(_ => Dispatcher.UIThread.Post(() => RunAsync().ContinueWith(_ => { })))
            .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return Results.All(r => r.Ok) ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        Results.Add((name, ok));
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  [{detail}]" : "")}");
    }

    private static async Task<bool> UntilAsync(Func<bool> condition, int ms = 2000)
    {
        for (var waited = 0; waited < ms; waited += 50)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }
        return condition();
    }

    private static T Find<T>(string name) where T : Control => _window.FindControl<T>(name)!;

    private static List<ResultRow> Rows => (Find<ItemsControl>("Results").ItemsSource as IEnumerable<object>)!.OfType<ResultRow>().ToList();

    private static string? Selected => Rows.FirstOrDefault(r => r.IsSelected)?.Title;

    private static string Clipboard => N.PasteboardText(Board) ?? "";

    private static string Toast => Find<TextBlock>("FooterText").Text ?? "";

    private static AppController Controller => AppController.Current!;

    private static void Shot(string name)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)_window.Bounds.Width, (int)_window.Bounds.Height));
        bitmap.Render(_window);
        bitmap.Save(Path.Combine(_shots, name + ".png"));
    }

    private static void Reopen() => Controller.Show(null);

    private static async Task RunAsync()
    {
        try
        {
            await Task.Delay(800);
            await StartupAsync();
            await HotkeyAsync();
            await UnlockAsync();
            await SearchAndKeysAsync();
            await CopyAsync();
            await DetailsAsync();
            await FillAndOpenAsync();
            await FocusAsync();
            await VaultChangeAsync();
            await LockingAsync();
            await TrayAsync();
            await TouchIdFlowAsync();
            RealEnclaveGate();
            await QuitAsync();
        }
        catch (Exception ex)
        {
            Check("run finished", false, ex.ToString());
        }

        Finish();
    }

    private static async Task StartupAsync()
    {
        await Task.Delay(1000);
        Check("background start does not keep focus", N.FrontPid != Environment.ProcessId, $"front: {N.FrontName}");
        _window = Controller.Window!;
        Check("window stays hidden in background start", !_window.IsVisible);
    }

    private static async Task HotkeyAsync()
    {
        Check("hotkey registers", Controller.HotkeyError is null, $"{Controller.HotkeyText} {Controller.HotkeyError}");
        Check("the vault in use is the fake one", Controller.Vault.State == VaultState.Locked && DesktopStore.Locate(Controller.Config.DataPath) == _vaultPath);
        Check("hotkey handler runs and opens the bar", N.PressHotkeyThroughHandler() && await UntilAsync(() => _window.IsVisible));
        Check("bar is the key window of the active app", await UntilAsync(() => N.AppIsActive && N.KeyWindow != 0), $"active={N.AppIsActive} key={N.KeyWindow != 0}");
        var key = N.KeyWindow;
        Check("bar is hidden from capture", key != 0 && N.SharingType(key) == 0, $"sharingType={(key != 0 ? N.SharingType(key) : -1)}");
        var behavior = key != 0 ? N.CollectionBehavior(key) : 0;
        Check("bar follows the active Space and full-screen apps", (behavior & 0x102) == 0x102, $"collectionBehavior=0x{behavior:x}");
        Check("locked bar focuses the password box", await UntilAsync(() => Find<TextBox>("PasswordBox").IsVisible && Find<TextBox>("PasswordBox").IsFocused));
        Shot("01-locked");

        N.PressHotkeyThroughHandler();
        Check("hotkey toggles the bar closed", await UntilAsync(() => !_window.IsVisible));
        N.PressHotkeyThroughHandler();
        await UntilAsync(() => _window.IsVisible && N.KeyWindow != 0 && Find<TextBox>("PasswordBox").IsFocused);
    }

    private static async Task UnlockAsync()
    {
        var box = Find<TextBox>("PasswordBox");
        N.Type("wrong");
        Check("typing reaches the password box", box.Text == "wrong", box.Text ?? "");
        N.Key(N.Enter, "\r");
        var error = Find<TextBlock>("MessageError");
        Check("wrong password is refused", await UntilAsync(() => error.IsVisible, 8000) && Controller.Vault.State == VaultState.Locked, error.Text ?? "");
        Shot("02-wrong-password");

        box.Text = "";
        N.Type(Password);
        N.Key(N.Enter, "\r");
        Check("right password unlocks", await UntilAsync(() => Controller.Vault.State == VaultState.Unlocked, 8000));
        Check("search box takes focus after unlock", await UntilAsync(() => Find<TextBox>("SearchBox").IsFocused));
        Shot("03-unlocked");
    }

    private static async Task SearchAndKeysAsync()
    {
        var search = Find<TextBox>("SearchBox");
        N.Type("github");
        Check("search filters", await UntilAsync(() => Rows.FirstOrDefault()?.Title == "GitHub"), string.Join(",", Rows.Select(r => r.Title)));
        Shot("04-search");
        N.Key(N.Delete, "\u007f");
        Check("backspace edits the search", search.Text == "githu", search.Text ?? "");
        N.Key(N.Escape, "\u001b");
        Check("Esc clears the search", await UntilAsync(() => search.Text == "") && _window.IsVisible);
        N.Type("azure");
        await UntilAsync(() => Rows.Count >= 2);
        var first = Selected;
        N.Key(N.Down, "");
        Check("arrow down moves the selection", await UntilAsync(() => Selected != first), $"{first} -> {Selected}");
        N.Key(N.Up, "");
        Check("arrow up moves it back", await UntilAsync(() => Selected == first));
        var help = Find<StackPanel>("HelpPanel");
        N.Chord('/', N.Command);
        Check("Cmd+/ opens help", await UntilAsync(() => help.IsVisible));
        Shot("05-help");
        N.Key(N.Escape, "\u001b");
        await UntilAsync(() => !help.IsVisible);
        N.Key(N.F1, "");
        Check("F1 opens help", await UntilAsync(() => help.IsVisible));
        N.Key(N.Escape, "\u001b");
        await UntilAsync(() => !help.IsVisible);
        search.Text = "";
    }

    private static async Task CopyAsync()
    {
        N.Type("github");
        await UntilAsync(() => Rows.FirstOrDefault()?.Title == "GitHub");
        N.Chord('c', N.Command);
        Check("Cmd+C copies the password", await UntilAsync(() => Clipboard == "gh-Secr3t!pass"), $"toast: {Toast}");
        Check("copied secret is marked concealed and transient", MacPlatform.ConcealedMarkers.All(type => N.PasteboardHasType(Board, type)));
        Check("copied secret clears after the configured time", await UntilAsync(() => Clipboard == "", 4000));

        N.Chord('c', N.Command | N.Shift);
        Check("Cmd+Shift+C copies the username", await UntilAsync(() => Clipboard == "alex-example"), Clipboard);
        N.Chord('c', N.Command | N.Option);
        Check("Cmd+Option+C copies the one-time code", await UntilAsync(() => Clipboard.Length == 6 && Clipboard.All(char.IsAsciiDigit)), Clipboard);
        N.Chord('p', N.Command);
        Check("Cmd+P copies the password", await UntilAsync(() => Clipboard == "gh-Secr3t!pass"), Clipboard);
        N.Chord('u', N.Command);
        Check("Cmd+U copies the username", await UntilAsync(() => Clipboard == "alex-example"), Clipboard);
        N.Chord('t', N.Command);
        Check("Cmd+T copies the one-time code", await UntilAsync(() => Clipboard.Length == 6), Clipboard);
        N.Chord('u', N.Command | N.Shift);
        Check("Cmd+Shift+U copies the website", await UntilAsync(() => Clipboard.Contains("github.com")), Clipboard);
    }

    private static async Task DetailsAsync()
    {
        var panel = Find<DockPanel>("DetailsPanel");
        N.Key(N.Tab, "\t");
        Check("Tab opens details", await UntilAsync(() => panel.IsVisible));
        Shot("06-details");
        var rows = (Find<ItemsControl>("Details").ItemsSource as IEnumerable<DetailRow>)!.ToList();
        var secret = rows.First(r => r.IsSecret);
        Check("secrets are masked", secret.Display.All(c => c == '•'), secret.Label);
        for (var i = 0; i < rows.Count && !secret.IsSelected; i++)
        {
            N.Key(N.Down, "");
            await Task.Delay(30);
        }
        N.Key(N.Space, " ");
        Check("Space reveals", await UntilAsync(() => secret.Display == "gh-Secr3t!pass"));
        N.Key(N.Enter, "\r");
        Check("Enter copies the field", await UntilAsync(() => Clipboard == "gh-Secr3t!pass"), Clipboard);
        N.Key(N.Escape, "\u001b");
        Check("Esc returns to the list", await UntilAsync(() => !panel.IsVisible));
    }

    private static async Task FillAndOpenAsync()
    {
        N.Key(N.Enter, "\r");
        Check("Enter without Accessibility falls back to copying", await UntilAsync(() => Toast.Contains("Can't auto-type")), Toast);

        Find<TextBox>("SearchBox").Text = "github";
        await UntilAsync(() => Rows.FirstOrDefault()?.Title == "GitHub");
        N.Key(N.Enter, "\r", N.Command);
        Check("Cmd+Enter opens the website", await UntilAsync(() => Platform.Opened.LastOrDefault()?.Contains("github.com") == true), Platform.Opened.LastOrDefault() ?? "");
        Check("opening a website closes the bar", await UntilAsync(() => !_window.IsVisible));

        Reopen();
        await UntilAsync(() => _window.IsVisible && N.KeyWindow != 0);
        N.Type("github");
        await UntilAsync(() => Rows.FirstOrDefault()?.Title == "GitHub");
        N.Chord('o', N.Command | N.Shift);
        Check("Cmd+Shift+O opens the web vault", await UntilAsync(() => Platform.Opened.LastOrDefault()?.Contains("#/vault?itemId=") == true), Platform.Opened.LastOrDefault() ?? "");

        // No steps: this proves the switch back to the previous app without sending a single keystroke to it.
        await UntilAsync(() => N.FrontPid != Environment.ProcessId, 1000);
        var target = Platform.CaptureForeground();
        if (target is null)
        {
            Console.WriteLine("SKIP  auto-type switch-back: this process was frontmost at capture");
            return;
        }
        Controller.Show(target);
        await UntilAsync(() => N.KeyWindow != 0 && N.FrontPid == Environment.ProcessId);
        var switched = await Platform.AutoTypeAsync(target, [], _window.Hide);
        Check("auto-type switches back to the previous app", switched == AutoTypeResult.Typed && N.FrontPid == target.Pid, $"target: {target.App}, front: {N.FrontName}");
    }

    private static async Task FocusAsync()
    {
        _window.Dismiss();
        await UntilAsync(() => N.FrontPid != Environment.ProcessId, 1000);
        Reopen();
        await UntilAsync(() => N.KeyWindow != 0);
        Check("opening takes focus", await UntilAsync(() => N.FrontPid == Environment.ProcessId), $"front: {N.FrontName}");
        N.Key(N.Escape, "\u001b");
        Check("Esc closes the bar", await UntilAsync(() => !_window.IsVisible));
        Check("closing gives focus back", await UntilAsync(() => N.FrontPid != Environment.ProcessId), $"front: {N.FrontName}");

        Reopen();
        await UntilAsync(() => N.KeyWindow != 0);
        N.Chord('w', N.Command);
        Check("Cmd+W closes the bar", await UntilAsync(() => !_window.IsVisible));
    }

    private static async Task VaultChangeAsync()
    {
        _fixture.AddLogin("Added by desktop sync", "new-user", "new-pass");
        _fixture.Write(_vaultPath, Password);
        Platform.UseFakeTouchId(Path.Combine(Paths.CacheDir, "touch-id-key.dat"));
        new QuickUnlockStore(Path.Combine(Paths.CacheDir, "quick-unlock.dat")).SaveDeclined(VaultFixture.UserId);
        Check("a rewritten data.json is picked up", await UntilAsync(() => Controller.Vault.Items.Any(i => i.Name == "Added by desktop sync"), 6000));
    }

    private static async Task UnlockAgainAsync()
    {
        var vault = Controller.Vault;
        if (vault.State == VaultState.Unlocked) return;
        vault.Probe();
        await vault.UnlockAsync(Password);
    }

    private static async Task LockingAsync()
    {
        var vault = Controller.Vault;
        await UnlockAgainAsync();
        Reopen();
        await UntilAsync(() => N.KeyWindow != 0);
        N.Chord('l', N.Command);
        Check("Cmd+L locks", await UntilAsync(() => vault.State == VaultState.Locked));
        Check("locked bar shows the password box", await UntilAsync(() => Find<TextBox>("PasswordBox").IsVisible));
        _window.Dismiss();

        await UnlockAgainAsync();
        _ = Controller.CopyAsync("still-secret", true);
        await UntilAsync(() => Clipboard == "still-secret");
        var sent = await Task.Run(() => Ipc.TrySend("lock"));
        Check("lock over IPC locks and clears the clipboard", sent && await UntilAsync(() => vault.State == VaultState.Locked && Clipboard == ""), Clipboard);

        await UnlockAgainAsync();
        await UntilAsync(() => !N.AppIsActive);
        Check("app is inactive while waiting for a system lock", !N.AppIsActive);
        N.PostFakeSystemLock();
        Check("system lock notification locks the vault", await UntilAsync(() => vault.State == VaultState.Locked));

        await UnlockAgainAsync();
        Console.WriteLine("waiting for the 1 minute idle lock");
        Check("idle timeout locks", await UntilAsync(() => vault.State == VaultState.Locked, 90_000));
    }

    private static void ClickTray(List<NativeMenuItem> items, string header)
    {
        // The managed handler only: AppKit does not expose the status item's menu, so a real click stays manual.
        var item = items.First(i => i.Header!.StartsWith(header));
        var raise = typeof(NativeMenuItem).GetInterfaces().SelectMany(i => i.GetMethods()).First(m => m.Name == "RaiseClicked");
        raise.Invoke(item, null);
    }

    private static async Task QuitAsync()
    {
        var lifetime = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        lifetime.Exit += (_, _) =>
        {
            Check("Cmd+Q quits with the copied secret cleared", Clipboard == "", Clipboard);
            Finish();
        };
        await UnlockAgainAsync();
        Reopen();
        await UntilAsync(() => N.KeyWindow != 0);
        _ = Controller.CopyAsync("still-secret", true);
        await UntilAsync(() => Clipboard == "still-secret");
        N.Chord('q', N.Command);
        await Task.Delay(3000);
        Check("Cmd+Q quits with the copied secret cleared", false, "app is still running");
    }

    private static async Task TouchIdFlowAsync()
    {
        var vault = Controller.Vault;
        var device = Platform.TouchIdDevice;
        var tray = TrayIcon.GetIcons(Application.Current!)![0];
        var items = tray.Menu!.Items.OfType<NativeMenuItem>().ToList();
        var toggle = items.FirstOrDefault(i => i.Header == "Unlock with Touch ID");
        Check("tray offers Touch ID, off while declined", toggle is { IsChecked: false }, string.Join(" | ", items.Select(i => i.Header)));
        if (toggle is null) return;

        await UnlockAgainAsync();
        var prompts = device.Prompts;
        ClickTray(items, "Unlock with Touch ID");
        Check("turning it on from the tray asks once and sets it up", await UntilAsync(() => vault.CanQuickUnlock) && device.Prompts == prompts + 1, $"prompts={device.Prompts - prompts}");

        vault.Lock();
        Reopen();
        Check("opening a locked bar asks for Touch ID and unlocks", await UntilAsync(() => vault.State == VaultState.Unlocked, 4000));
        Check("the bar stays open with the search box focused", await UntilAsync(() => _window.IsVisible && Find<TextBox>("SearchBox").IsFocused));
        _window.Dismiss();

        vault.Lock();
        device.NextResult = -2;
        Reopen();
        Check("canceling Touch ID leaves the password box", await UntilAsync(() => Find<TextBox>("PasswordBox").IsVisible && Find<TextBox>("PasswordBox").IsFocused, 4000) && vault.State == VaultState.Locked);
        Check("canceling keeps the setup", vault.CanQuickUnlock);
        device.NextResult = 0;
        N.Chord('r', N.Command);
        Check("Cmd+R retries Touch ID", await UntilAsync(() => vault.State == VaultState.Unlocked, 4000));
        Check("Cmd+R keeps the bar open", _window.IsVisible && N.AppIsActive);
        _window.Dismiss();

        await UnlockAgainAsync();
        ClickTray(items, "Unlock with Touch ID");
        Check("turning it off forgets the setup", await UntilAsync(() => vault.QuickUnlockSetting == QuickUnlockSetting.Off));
    }

    /// <summary>The only step that uses the real enclave. It never prompts: creating a key asks nothing, and using it is refused without a finger.</summary>
    private static void RealEnclaveGate()
    {
        var native = new NativeTouchId();
        if (!native.Available)
        {
            Console.WriteLine("SKIP  real enclave: no Secure Enclave or no enrolled finger here");
            return;
        }

        var created = native.Create(out var blob);
        if (created == -25308)
        {
            Console.WriteLine("SKIP  real enclave: the screen is locked");
            return;
        }

        Check("the Secure Enclave creates a key for this ad-hoc build", created == 0, $"status={created}");
        if (created != 0) return;
        var refused = NativeTouchId.Derive(blob, new byte[32], "e2e", prompt: false, out var secret);
        Check("the enclave refuses that key without Touch ID", refused != 0 && secret.Length == 0, $"status={refused}");
    }

    private static void Finish()
    {
        var failed = Results.Count(r => !r.Ok);
        Console.WriteLine($"\n{Results.Count - failed} passed, {failed} failed");
        Environment.Exit(failed == 0 ? 0 : 1);
    }

    private static async Task TrayAsync()
    {
        var classes = N.WindowClasses();
        Check("status item exists", classes.Any(c => c.Contains("StatusBar", StringComparison.OrdinalIgnoreCase)), string.Join(",", classes));

        var tray = TrayIcon.GetIcons(Application.Current!)![0];
        var items = tray.Menu!.Items.OfType<NativeMenuItem>().ToList();
        Check("tray menu has search, lock and quit",
            items.Any(i => i.Header!.StartsWith("Search")) && items.Any(i => i.Header == "Lock vault") && items.Any(i => i.Header == "Quit Vaultbar"),
            string.Join(" | ", items.Select(i => i.Header)));
        Check("tray tooltip names the hotkey", tray.ToolTipText?.Contains("Cmd") == true && tray.ToolTipText.Contains("Space"), tray.ToolTipText ?? "");

        var button = N.StatusButton();
        var height = button != 0 ? N.StatusImageHeight(button) : 0;
        Check("status item shows a menu-bar sized image", height is > 0 and <= 24, $"height={height}");

        await UnlockAgainAsync();
        ClickTray(items, "Lock vault");
        Check("tray Lock vault locks", await UntilAsync(() => Controller.Vault.State == VaultState.Locked));
        ClickTray(items, "Search vault");
        Check("tray Search vault opens the bar", await UntilAsync(() => _window.IsVisible && N.KeyWindow != 0));
        _window.Dismiss();
    }
}
